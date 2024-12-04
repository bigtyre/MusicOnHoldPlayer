using Microsoft.Extensions.Logging;

class MediaLibrary(string directory, ILogger logger) : IDisposable
{
    private readonly ILogger _logger = logger;
    private readonly List<Stream> _tracks8khz = [];
    private bool _isDisposed;

    public async Task LoadTracksAsync()
    {
        await LoadTracksAsync(directory, _tracks8khz);
        //await LoadTracksAsync(Path.Combine(Directory, "16khz"), Tracks16khz);
    }

    public async Task LoadTracksAsync(string sourceDir, ICollection<Stream> target)
    {
        _logger.LogInformation("Loading tracks from {sourceDir}", sourceDir);

        // Load all tracks into memory, to avoid reading from disc when a call is answered
        var files = Directory.GetFiles(sourceDir, "*.wav");

        var numFiles = files.Length;
        _logger.LogInformation("# .wav files found in directory: {numFiles}", numFiles);

        int i = 0;
        target.Clear();
        foreach (var filename in files)
        {
            i++;
            _logger.LogInformation("Loading track {i}/{numFiles}: '{filename}'", i, numFiles, filename);

            using var fileStream = File.OpenRead(filename);
            var track = new MemoryStream();
            await fileStream.CopyToAsync(track);
            target.Add(track);
        }

        var numTracks = target.Count;
        _logger.LogInformation("Finished loading tracks. # tracks loaded: {numTracks}", numTracks);
    }
    
    public int GetTrackCount()
    {
        return _tracks8khz.Count;
    }


    public async Task<MemoryStream> GetTrackAsync(int index)
    {
        var clone = await GetTrackCloneAsync(_tracks8khz, index);
        return clone;
    }

    public static async Task<MemoryStream> GetTrackCloneAsync(IList<Stream> tracks, int index)
    {
        var track = tracks[index];

        // Create an in-memory copy so that each instance can be read independently of the others.
        // I realise this is not very efficient in terms of memory use.
        var trackCopy = new MemoryStream();
        track.Position = 0;

        await track.CopyToAsync(trackCopy);
        trackCopy.Position = 0;
        return trackCopy;
    }

    protected virtual void Dispose(bool isDisposing)
    {
        if (_isDisposed)
            return;
        
        if (isDisposing)
        {
            var tracks = _tracks8khz.ToList();
            foreach (var track in tracks)
            {
                track.Dispose();
            }
        }

        _isDisposed = true;
    }

    public void Dispose()
    {
        // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        Dispose(isDisposing: true);
        GC.SuppressFinalize(this);
    }
}