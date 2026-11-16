using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Windows.Media.Control;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace DynamicIsland;

internal sealed record MediaState(string Title = "", string Artist = "", byte[]? Cover = null,
    bool Active = false, bool Playing = false, bool Previous = false, bool Toggle = false, bool Next = false);

internal sealed class Media : IDisposable
{
    public enum Command { Previous, Toggle, Next, ReleaseLocal, OpenFile }

    private readonly record struct Request(Command Command, string? Path = null);
    private readonly Channel<Request> requests = Channel.CreateUnbounded<Request>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true
    });
    private readonly CancellationTokenSource shutdown = new();
    private readonly Task worker;
    private MediaState state = new();
    private byte[]? cachedCover;
    private (string Source, string Title, string Artist) coverKey;
    private long lastCoverAttempt;

    public Media() => worker = Task.Run(() => RunAsync(shutdown.Token));

    public MediaState State => Volatile.Read(ref state);
    public void Send(Command command) => requests.Writer.TryWrite(new(command));
    public void Open(string path) => requests.Writer.TryWrite(new(Command.OpenFile, path));

    private async Task RunAsync(CancellationToken stop)
    {
        GlobalSystemMediaTransportControlsSessionManager? manager = null;
        MediaPlayer? player = null;
        MediaSource? source = null;
        string localTitle = "", lastSource = "";
        Request? request = null;
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var snapshot = new MediaState();
                try
                {
                    if (request is { Command: Command.ReleaseLocal })
                    {
                        player?.Dispose();
                        player = null;
                        source?.Dispose();
                        source = null;
                    }
                    else if (request is { Command: Command.OpenFile, Path: { } path })
                    {
                        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(stop);
                        var replacement = MediaSource.CreateFromStorageFile(file);
                        var localPlayer = player;
                        try
                        {
                            localPlayer ??= new MediaPlayer();
                            localPlayer.Source = replacement;
                        }
                        catch
                        {
                            if (player is null)
                                localPlayer?.Dispose();
                            replacement.Dispose();
                            throw;
                        }
                        player = localPlayer;
                        source?.Dispose();
                        source = replacement;
                        localTitle = Path.GetFileNameWithoutExtension(path);
                        player.Play();
                    }

                    if (player is not null)
                    {
                        var playback = player.PlaybackSession;
                        if (request is { Command: Command.Toggle })
                        {
                            if (playback.PlaybackState == MediaPlaybackState.Playing)
                                player.Pause();
                            else
                                player.Play();
                        }
                        snapshot = new(localTitle, "Local audio", Active: true, Toggle: true,
                            Playing: playback.PlaybackState == MediaPlaybackState.Playing);
                    }
                    else
                    {
                        manager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(stop);
                        var session = SelectSession(manager, lastSource);
                        if (session is not null)
                        {
                            lastSource = session.SourceAppUserModelId;
                            await ControlAsync(session, request?.Command, stop);
                            snapshot = await ReadSessionAsync(session, lastSource, stop);
                        }
                    }
                }
                catch (Exception error) when (IsMediaError(error))
                {
                    manager = null;
                }

                Volatile.Write(ref state, snapshot);
                using var poll = CancellationTokenSource.CreateLinkedTokenSource(stop);
                poll.CancelAfter(540);
                try { request = await requests.Reader.ReadAsync(poll.Token); }
                catch (OperationCanceledException) when (!stop.IsCancellationRequested) { request = null; }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            player?.Dispose();
            source?.Dispose();
        }
    }

    private static GlobalSystemMediaTransportControlsSession? SelectSession(
        GlobalSystemMediaTransportControlsSessionManager manager, string lastSource)
    {
        var session = manager.GetCurrentSession();
        if (IsPlaying(session))
            return session;

        foreach (var candidate in manager.GetSessions())
        {
            if (IsPlaying(candidate))
                return candidate;
            if (session is null || candidate.SourceAppUserModelId == lastSource)
                session = candidate;
        }
        return session;
    }

    private static bool IsPlaying(GlobalSystemMediaTransportControlsSession? session) =>
        session?.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

    private static async Task ControlAsync(GlobalSystemMediaTransportControlsSession session, Command? command,
        CancellationToken stop)
    {
        switch (command)
        {
            case Command.Previous: await session.TrySkipPreviousAsync().AsTask(stop); break;
            case Command.Next: await session.TrySkipNextAsync().AsTask(stop); break;
            case Command.Toggle:
                if (!await session.TryTogglePlayPauseAsync().AsTask(stop))
                {
                    if (IsPlaying(session))
                        await session.TryPauseAsync().AsTask(stop);
                    else
                        await session.TryPlayAsync().AsTask(stop);
                }
                break;
        }
    }

    private async Task<MediaState> ReadSessionAsync(GlobalSystemMediaTransportControlsSession session,
        string source, CancellationToken stop)
    {
        var info = session.GetPlaybackInfo();
        var controls = info?.Controls;
        bool playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        var properties = await session.TryGetMediaPropertiesAsync().AsTask(stop);
        if (properties is not null)
        {
            var key = (source, properties.Title, properties.Artist);
            if (key != coverKey || (cachedCover is null && Stopwatch.GetElapsedTime(lastCoverAttempt).TotalSeconds >= 2))
            {
                coverKey = key;
                cachedCover = await ReadCoverAsync(properties.Thumbnail, stop);
                lastCoverAttempt = Stopwatch.GetTimestamp();
            }
        }

        return new(properties?.Title ?? "", properties?.Artist ?? "", properties is null ? null : cachedCover,
            Active: true, Playing: playing, Previous: controls?.IsPreviousEnabled == true,
            Toggle: controls?.IsPlayPauseToggleEnabled == true ||
                (playing ? controls?.IsPauseEnabled : controls?.IsPlayEnabled) == true,
            Next: controls?.IsNextEnabled == true);
    }

    private static async Task<byte[]?> ReadCoverAsync(IRandomAccessStreamReference? thumbnail, CancellationToken stop)
    {
        if (thumbnail is null)
            return null;

        try
        {
            using var stream = await thumbnail.OpenReadAsync().AsTask(stop);
            ulong size = stream.Size;
            if (size is 0 or > 2 * 1024 * 1024)
                return null;

            using var reader = new DataReader(stream);
            if (await reader.LoadAsync((uint)size).AsTask(stop) != (uint)size)
                return null;

            var bytes = new byte[(int)size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception error) when (IsMediaError(error))
        {
            return null;
        }
    }

    private static bool IsMediaError(Exception error) => error is ExternalException or IOException or
        UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException;

    public void Dispose()
    {
        shutdown.Cancel();
        worker.GetAwaiter().GetResult();
        shutdown.Dispose();
    }
}
