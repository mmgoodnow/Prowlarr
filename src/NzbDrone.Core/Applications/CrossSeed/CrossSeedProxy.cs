using System;
using System.Collections.Generic;
using System.Net.Http;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Common.Serializer;

namespace NzbDrone.Core.Applications.CrossSeed
{
    public interface ICrossSeedProxy
    {
        CrossSeedStatus GetStatus(CrossSeedSettings settings);
        List<CrossSeedIndexer> GetIndexers(CrossSeedSettings settings);
        CrossSeedIndexer AddIndexer(CrossSeedIndexer indexer, CrossSeedSettings settings);
        CrossSeedIndexer UpdateIndexer(CrossSeedIndexer indexer, CrossSeedSettings settings);
        void RemoveIndexer(int id, CrossSeedSettings settings);
        CrossSeedTestResult TestIndexer(CrossSeedIndexer indexer, CrossSeedSettings settings);
    }

    public class CrossSeedProxy : ICrossSeedProxy
    {
        private const string AppApiRoute = "/api/indexer/v1";

        private readonly IHttpClient _httpClient;
        private readonly Logger _logger;

        public CrossSeedProxy(IHttpClient httpClient, Logger logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public CrossSeedStatus GetStatus(CrossSeedSettings settings)
        {
            var request = BuildRequest(settings, $"{AppApiRoute}/status", HttpMethod.Get);
            return Execute<CrossSeedStatus>(request);
        }

        public List<CrossSeedIndexer> GetIndexers(CrossSeedSettings settings)
        {
            var request = BuildRequest(settings, AppApiRoute + "?includeInactive=true", HttpMethod.Get);
            return Execute<List<CrossSeedIndexer>>(request);
        }

        public CrossSeedIndexer AddIndexer(CrossSeedIndexer indexer, CrossSeedSettings settings)
        {
            var request = BuildRequest(settings, AppApiRoute, HttpMethod.Post);
            request.SetContent(indexer.ToJson());
            return Execute<CrossSeedIndexer>(request);
        }

        public CrossSeedIndexer UpdateIndexer(CrossSeedIndexer indexer, CrossSeedSettings settings)
        {
            var request = BuildRequest(settings, $"{AppApiRoute}/{indexer.Id}", HttpMethod.Put);
            request.SetContent(indexer.ToJson());
            return Execute<CrossSeedIndexer>(request);
        }

        public void RemoveIndexer(int id, CrossSeedSettings settings)
        {
            var request = BuildRequest(settings, $"{AppApiRoute}/{id}", HttpMethod.Delete);
            Execute<object>(request);
        }

        public CrossSeedTestResult TestIndexer(CrossSeedIndexer indexer, CrossSeedSettings settings)
        {
            var request = BuildRequest(settings, $"{AppApiRoute}/test", HttpMethod.Post);

            var testPayload = new
            {
                url = indexer.Url,
                apikey = indexer.ApiKey,
                id = indexer.Id > 0 ? indexer.Id : (int?)null
            };

            request.SetContent(testPayload.ToJson());
            return Execute<CrossSeedTestResult>(request);
        }

        private HttpRequest BuildRequest(CrossSeedSettings settings, string resource, HttpMethod method)
        {
            var baseUrl = settings.BaseUrl.TrimEnd('/');

            var request = new HttpRequestBuilder(baseUrl)
                .Resource(resource)
                .Accept(HttpAccept.Json)
                .SetHeader("X-Api-Key", settings.ApiKey)
                .Build();

            request.Headers.ContentType = "application/json";
            request.Method = method;
            request.AllowAutoRedirect = true;

            return request;
        }

        private TResource Execute<TResource>(HttpRequest request)
             where TResource : new()
        {
            var response = _httpClient.Execute(request);

            if ((int)response.StatusCode >= 300)
            {
                throw new HttpException(response);
            }

            return Json.Deserialize<TResource>(response.Content);
        }
    }
}
