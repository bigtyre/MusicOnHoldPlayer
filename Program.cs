using BigTyre.Phones.MusicOnHoldPlayer.Configuration;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Security;
using SIPSorcery.Media;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using SIPSorceryMedia.Abstractions;
using System.Net;
using System.Reflection.Metadata.Ecma335;

// Set up logging
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
var logger = loggerFactory.CreateLogger<Program>();

// Set up handlers for unhandled exceptions and process exit
AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
{
    logger.LogCritical("An unhandled exception caused the application to fail:\n{errorMessage}", args.ExceptionObject);
};

AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
{
    logger.LogInformation("Application exiting.");
};


// Start the application
logger.LogInformation("Application started.");


// Load configuration settings
logger.LogInformation("Loading application configuration");
var settings = ConfigurationHandler.GetAppSettings();

var mediaDirectory = settings.MediaDirectory ?? throw new ConfigurationException("Media directory not configured.");

var accounts = settings.Accounts;
var numAccounts = accounts.Count;
if (numAccounts < 1) throw new ConfigurationException("No accounts configured.");

var pbxIpText = settings.PBXIPAddress ?? throw new ConfigurationException("PBX IP not configured.");
var pbxIp = IPAddress.Parse(pbxIpText);
var pbxPort = (int)(settings.PBXPort ?? 5060);
var realm = settings.PBXAuthenticationRealm ?? throw new ConfigurationException("PBX Authentication realm not configured.");
var registrationExpirySeconds = (int)(settings.SIPRegistrationExpirySeconds ?? 90);

logger.LogInformation("Configuration loaded");


// Load tracks
logger.LogInformation("Loading tracks from media library.");
var mediaLibrary = new MediaLibrary(mediaDirectory, loggerFactory.CreateLogger<MediaLibrary>());
await mediaLibrary.LoadTracksAsync();

if (mediaLibrary.GetTrackCount() < 1)
{
    throw new ConfigurationException("Directory does not contain any tracks.");
}

// Get the client IP address
logger.LogInformation("Finding client IP address for SIP registration.");

var hostname = Dns.GetHostName();
var clientIp = Dns.GetHostEntry(hostname)
   .AddressList
   .First(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

logger.LogInformation("IP Address found: {clientIp}", clientIp);


// Create the manual reset event used to shutdown the application
var exitMRE = new ManualResetEvent(false);
var callLock = new object();

var activeCalls = new Dictionary<string, SIPUserAgent>();
int nextUserAgentId = 0;

// Set up the SIP channels for each account
logger.LogInformation("Setting up SIP registration for {numAccounts} account{numAccountsPlural}", numAccounts, numAccounts == 1 ? "" : "s");
var transports = new List<SIPTransport>();
var sipUserAgents = new List<SIPRegistrationUserAgent>();
foreach (var account in accounts)
{
    var username = account.Username;
    var clientExtension = account.Extension;
    var password = account.Password;

    logger.LogInformation("Setting up SIP transports for account: {extension}", clientExtension);

    // Set up a default SIP transport.
    var sipTransport = new SIPTransport();
    sipTransport.AddSIPChannel(sipTransport.CreateChannel(SIPProtocolsEnum.tcp, System.Net.Sockets.AddressFamily.InterNetwork));
    sipTransport.AddSIPChannel(sipTransport.CreateChannel(SIPProtocolsEnum.tcp, System.Net.Sockets.AddressFamily.InterNetwork));

    sipTransport.EnableTraceLogs();
    transports.Add(sipTransport);

    var pbxIPEndpoint = new IPEndPoint(pbxIp, pbxPort);
    var outboundProxy = new SIPEndPoint(SIPProtocolsEnum.tcp, pbxIPEndpoint);
    var sipUri = new SIPURI(clientExtension, clientIp.ToString(), "", SIPSchemesEnum.sip);
    var registrarHost = pbxIp;
    var contactUri = sipUri;
    var customHeaders = Array.Empty<string>();

    // Create a client user agent to maintain a periodic registration with a SIP server.
    //var regUserAgent = new SIPRegistrationUserAgent(sipTransport, username, password, server, expiry);
    var regUserAgent = new SIPRegistrationUserAgent(
        sipTransport: sipTransport,
        outboundProxy: outboundProxy,
        sipAccountAOR: sipUri,
        authUsername: username,
        password: password,
        realm: realm,
        registrarHost: registrarHost.ToString(),
        contactURI: contactUri,
        expiry: registrationExpirySeconds,
        customHeaders
    );

    // Event handlers for the different stages of the registration.
    regUserAgent.RegistrationFailed += (sipUri, sipResponse, err) =>
    {
        logger.LogError("SIP registration failed permanently on extension {extension} - {sipUri}: {errorMessage}", clientExtension, sipUri, err);
        exitMRE.Set();
    };

    regUserAgent.RegistrationTemporaryFailure += (sipUri, sipResponse, msg) => {
        logger.LogWarning("SIP registration failed temporarily on extension {extension} - {sipUri}: {msg}", clientExtension, sipUri, msg);
    };

    regUserAgent.RegistrationRemoved += (sipUri, sipResponse) =>
    {
        logger.LogWarning("SIP registration removed on extension {extension} - {sipUri}: {msg}", clientExtension, sipUri, sipResponse.ReasonPhrase);
    };

    regUserAgent.RegistrationSuccessful += (sipUri, sipResponse) =>
    {
        logger.LogInformation("SIP registration succeeded on extension {extension}. {msg}", clientExtension, sipResponse.ReasonPhrase);
    };

    // Start the thread to perform the initial registration and then periodically resend it.
    regUserAgent.Start();
    sipUserAgents.Add(regUserAgent);

    var activeAgents = new HashSet<SIPUserAgent>();

    var ua1 = CreateUserAgent(activeAgents, sipTransport, outboundProxy);
    var ua2 = CreateUserAgent(activeAgents, sipTransport, outboundProxy);
}


Console.CancelKeyPress += (object? sender, ConsoleCancelEventArgs e) =>
{
    e.Cancel = true;
    exitMRE.Set();
};

logger.LogInformation("Application start up completed");

exitMRE.WaitOne();

logger.LogInformation("Application shutting down");

ShutdownSIPUserAgents();
ShutdownSIPTransports();

logger.LogInformation("Application shutdown completed.");




void ShutdownSIPUserAgents()
{
    logger.LogInformation("Stopping SIP user agents");
    int numAgents = sipUserAgents.Count;
    int i = 0;
    foreach (var regUserAgent in sipUserAgents)
    {
        logger.LogInformation("Stopping SIP user agent {i}/{numAgents}", i, numAgents);
        regUserAgent.Stop();
    }

    // Allow for un-register request to be sent (REGISTER with 0 expiry)
    logger.LogInformation("Waiting 2 seconds to allow user agents to unregister");
    Task.Delay(2000).Wait();

    logger.LogInformation("Finished shutting down SIP user agents");
}

void ShutdownSIPTransports()
{
    int numTransports = transports.Count;
    int i = 0;
    foreach (var sipTransport in transports)
    {
        logger.LogInformation("Shutting down SIP transport {i}/{numTransports}", i, numTransports);
        sipTransport.Shutdown();
    }
}

SIPUserAgent CreateUserAgent(
    HashSet<SIPUserAgent> activeAgents,
    SIPTransport sipTransport,
    SIPEndPoint outboundProxy
)
{
    var userAgentId = nextUserAgentId++;

    var userAgent = new SIPUserAgent(sipTransport, outboundProxy);

    userAgent.ServerCallCancelled += (uas, cancelRequest) =>
    {
        logger.LogDebug("Incoming call cancelled by remote party on user agent {userAgentId}.", userAgentId);
        activeAgents.Remove(userAgent);
    };

    userAgent.OnCallHungup += (dialog) =>
    {
        logger.LogDebug("Call hung up by remote party on user agent {userAgentId}.", userAgentId);
        activeAgents.Remove(userAgent);
    };

    userAgent.OnIncomingCall += async (ua, req) =>
    {
        var callInfo = req.Header.CallInfo;
        var callId = req.Header.CallId;
        
        logger.LogDebug("Incoming call {callId} {callInfo}", callId, callInfo);
        lock (callLock) { 
            if (activeCalls.ContainsKey(callId))
            {
                logger.LogInformation("Call {callId} already handled by another UA. Returning...", callId);
                return;
            }

            activeCalls.Add(callId, ua);
            activeAgents.Add(ua);
        }

        var allowedCodecs = new[]
        {
                AudioCodecsEnum.PCMA,
                AudioCodecsEnum.PCMU,
                AudioCodecsEnum.G722,
            };

        var encoder = new AudioEncoder();
        MediaEndPoints mediaEndPoints = new()
        {
        };

        logger.LogDebug("Attempting to accept call {callId}", callId);
        var uas = userAgent.AcceptCall(req);
        if (uas == null) 
            return;

        var voipMediaSession = new VoIPMediaSession(mediaEndPoints)
        {
            AcceptRtpFromAny = true,
        };

        var format = new AudioFormat(SDPWellKnownMediaFormatsEnum.PCMA);
        voipMediaSession.addTrack(new SIPSorcery.Net.MediaStreamTrack(SDPWellKnownMediaFormatsEnum.PCMU));
        voipMediaSession.AudioExtrasSource.SetAudioSourceFormat(format);
        await voipMediaSession.AudioExtrasSource.StartAudio();

        logger.LogInformation("Call incoming.");
        var answered = await userAgent.Answer(uas, voipMediaSession);
        logger.LogInformation("Call {answerStatus}", answered ? "answered" : "not answered");

        
        // If there are two callers connected, transfer them to each other.
        if (activeAgents.Count == 2)
        {
            logger.LogDebug("2 active agents! Time to transfer the calls!");
            var agents = activeAgents.ToList();

            var ua1 = agents[0];
            var ua2 = agents[1];

            if (ua1 == userAgent)
            {
                await TransferFrom(ua1, ua2);
            }
            else
            {
                await TransferFrom(ua2, ua1);
            }

            return;
        }

        try
        {
            voipMediaSession.AudioExtrasSource.SetSource(AudioSourcesEnum.Silence);
        }
        catch (Exception ex)
        {
            logger.LogError("An error occurred while setting audio source to silence: {exceptionMessage}", ex.Message);
        }

        var f = voipMediaSession.AudioExtrasSource.GetAudioSourceFormats();

        var selectedCodec = format;

        var is8khz = selectedCodec.ClockRate == 8000;
        logger.LogDebug("Sending audio in {frequency}khz", is8khz ? 8 : 16);

        //var tracks = is8khz ? mediaLibrary.Tracks8khz : mediaLibrary.Tracks16khz;
        var sampleRate = is8khz ? AudioSamplingRatesEnum.Rate8KHz : AudioSamplingRatesEnum.Rate16KHz;

        // Randomise the order the tracks will be played in.
        var random = Random.Shared;
        var numTracks = mediaLibrary.GetTrackCount();
        var indices = Enumerable.Range(0, numTracks).ToList();
        var shuffledIndices = indices.OrderBy(r => random.Next()).ToList();
        var index = 0;

        // Play the tracks in the selected order until the call ends.
        while (userAgent.IsCallActive)
        {
            var trackNumber = shuffledIndices[index];

            // Get the track to play
            using var onHoldMusic = await mediaLibrary.GetTrackAsync(trackNumber);

            // Play the track
            await voipMediaSession.AudioExtrasSource.SendAudioFromStream(onHoldMusic, sampleRate);

            // When the track ends, set the audio back to silence
            voipMediaSession.AudioExtrasSource.SetSource(AudioSourcesEnum.Silence);

            index++;
            index %= shuffledIndices.Count;
        }
    };
    return userAgent;
}

async Task TransferFrom(SIPUserAgent ua1, SIPUserAgent ua2)
{
    int maxAttempts = 3;
    int attempts = 0;
    bool retry = true;
    while (retry)
    {
        attempts++;
        if (await ua1.AttendedTransfer(ua2.Dialogue, TimeSpan.FromSeconds(5), CancellationToken.None))
        {
            logger.LogInformation("Call transferred successfully. Waiting 1 sec, then hanging up.");
            await Task.Delay(1000);
            ua1.Hangup();
            return;
        }

        logger.LogInformation("Call transfer failed.");
        if (retry)
        {
            logger.LogInformation("Waiting 500ms before attempting again {attempts}/{maxAttempts}.", attempts, maxAttempts);
            retry = attempts < maxAttempts + 1;

            await Task.Delay(500);
        }
    }
}