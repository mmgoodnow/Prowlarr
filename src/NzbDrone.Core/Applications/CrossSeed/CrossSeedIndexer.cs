using Newtonsoft.Json;

namespace NzbDrone.Core.Applications.CrossSeed
{
    public class CrossSeedIndexer
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("url")]
        public string Url { get; set; }

        [JsonProperty("apikey")]
        public string ApiKey { get; set; }

        [JsonProperty("active")]
        public bool Active { get; set; }
    }
}
