namespace NzbDrone.Core.Applications.CrossSeed
{
    public class CrossSeedStatus
    {
        public string Version { get; set; }
        public string AppName { get; set; }
        public int IndexerCount { get; set; }
        public int ActiveIndexers { get; set; }
    }
}
