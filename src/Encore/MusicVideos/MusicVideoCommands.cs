using NzbDrone.Core.Messaging.Commands;

namespace Encore.MusicVideos
{
    // Lidarr tasks, so they show in the status at the bottom left and under System > Tasks.
    // Type exclusive, so music videos never take more than one of Lidarr's task threads each.

    /// <summary>
    /// Looks for an artist's music videos again: drops ones today's rules leave out and adds new ones.
    /// Runs after Lidarr refreshes an artist.
    /// </summary>
    public class MusicVideoRefreshCommand : Command
    {
        // First, as Lidarr compares commands property by property to drop duplicates
        public List<int> ArtistIds { get; set; } = [];

        public override bool SendUpdatesToClient => true;
        public override bool IsTypeExclusive => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>
    /// Downloads an artist's missing monitored videos, or the given videos whatever their state.
    /// </summary>
    public class MusicVideoSearchCommand : Command
    {
        public int ArtistId { get; set; }
        public List<string>? VideoKeys { get; set; }

        public override bool SendUpdatesToClient => true;
        public override bool IsTypeExclusive => true;
        public override bool UpdateScheduledTask => false;
    }

    /// <summary>
    /// Adds a video, channel or playlist pasted on the Videos page. A single video is downloaded right away.
    /// </summary>
    public class MusicVideoAddCommand : Command
    {
        public int ArtistId { get; set; }
        public string Url { get; set; } = string.Empty;

        public override bool SendUpdatesToClient => true;
        public override bool IsTypeExclusive => true;
        public override bool UpdateScheduledTask => false;
    }
}
