using System;
using System.Collections.Generic;
using System.Linq;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Indexers;

namespace NzbDrone.Core.Applications.CrossSeed
{
    public class CrossSeed : ApplicationBase<CrossSeedSettings>
    {
        public override string Name => "cross-seed";

        private readonly ICrossSeedProxy _crossSeedProxy;
        private readonly IConfigFileProvider _configFileProvider;

        public CrossSeed(ICrossSeedProxy crossSeedProxy, IConfigFileProvider configFileProvider, IAppIndexerMapService appIndexerMapService, IIndexerFactory indexerFactory, Logger logger)
            : base(appIndexerMapService, indexerFactory, logger)
        {
            _crossSeedProxy = crossSeedProxy;
            _configFileProvider = configFileProvider;
        }

        public override ValidationResult Test()
        {
            var failures = new List<ValidationFailure>();

            try
            {
                failures.AddIfNotNull(_crossSeedProxy.TestConnection(Settings));
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to complete application test");
                failures.AddIfNotNull(new ValidationFailure("BaseUrl", $"Unable to complete application test, cannot connect to cross-seed. {ex.Message}"));
            }

            return new ValidationResult(failures);
        }

        public override List<AppIndexerMap> GetIndexerMappings()
        {
            _logger.Debug("CrossSeed: Getting indexer mappings...");
            
            // Get existing mappings from Prowlarr's database
            var existingMappings = _appIndexerMapService.GetMappingsForApp(Definition.Id);
            _logger.Debug("CrossSeed: Found {0} existing mappings in database", existingMappings.Count);
            
            // Get ALL indexers from cross-seed (including inactive ones)
            var allIndexers = _crossSeedProxy.GetIndexers(Settings);
            _logger.Debug("CrossSeed: Found {0} total indexers in cross-seed", allIndexers.Count);
            
            var mappings = new List<AppIndexerMap>();

            // Only return remote indexers that have corresponding mappings in Prowlarr's database
            // This implements the "mapping hack" - disabled indexers (have mappings) are returned,
            // but deleted indexers (no mappings) are filtered out
            foreach (var mapping in existingMappings)
            {
                var remoteIndexer = allIndexers.FirstOrDefault(i => i.Id == mapping.RemoteIndexerId);
                if (remoteIndexer != null)
                {
                    // Validate mapping integrity by checking if URL has correct tail pattern
                    var expectedTail = $"/{mapping.IndexerId}/api";
                    if (!remoteIndexer.Url.EndsWith(expectedTail))
                    {
                        _logger.Warn("CrossSeed: Mapping corruption detected! Prowlarr ID {0} -> cross-seed ID {1} has URL '{2}' but expected tail '{3}'. Attempting URL-based fallback...", 
                            mapping.IndexerId, mapping.RemoteIndexerId, remoteIndexer.Url, expectedTail);
                        
                        // Try to find indexer by URL tail pattern instead of ID (fallback)
                        var correctIndexer = allIndexers.FirstOrDefault(i => i.Url.EndsWith(expectedTail));
                        if (correctIndexer != null)
                        {
                            _logger.Info("CrossSeed: URL fallback successful! Correcting mapping: Prowlarr ID {0} -> cross-seed ID {1} (was {2})", 
                                mapping.IndexerId, correctIndexer.Id, mapping.RemoteIndexerId);
                            
                            // Update the mapping in database to fix corruption
                            var updatedMapping = new AppIndexerMap
                            {
                                Id = mapping.Id,
                                AppId = mapping.AppId,
                                IndexerId = mapping.IndexerId,
                                RemoteIndexerId = correctIndexer.Id,
                                RemoteIndexerName = correctIndexer.Name
                            };
                            _appIndexerMapService.Update(updatedMapping);
                            
                            mappings.Add(updatedMapping);
                        }
                        else
                        {
                            _logger.Error("CrossSeed: URL fallback failed! No indexer found with expected tail '{0}'. Mapping will be excluded from sync.", expectedTail);
                        }
                    }
                    else
                    {
                        // Mapping is valid
                        _logger.Debug("CrossSeed: Including mapped indexer: Prowlarr ID {0} -> cross-seed ID {1} ({2}) [Active: {3}]", 
                            mapping.IndexerId, remoteIndexer.Id, remoteIndexer.Name, remoteIndexer.Active);
                        
                        mappings.Add(new AppIndexerMap
                        {
                            IndexerId = mapping.IndexerId,
                            RemoteIndexerId = remoteIndexer.Id,
                            RemoteIndexerName = remoteIndexer.Name
                        });
                    }
                }
                else
                {
                    _logger.Debug("CrossSeed: Mapped indexer not found in remote: Prowlarr ID {0} -> cross-seed ID {1}", 
                        mapping.IndexerId, mapping.RemoteIndexerId);
                }
            }

            _logger.Debug("CrossSeed: Returning {0} filtered mappings (disabled indexers included, deleted ones excluded)", mappings.Count);
            return mappings;
        }

        private bool TryExtractIndexerIdFromUrl(string url, out int indexerId)
        {
            indexerId = 0;

            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            try
            {
                var uri = new Uri(url);
                var pathSegments = uri.AbsolutePath.Trim('/').Split('/');

                // Expected format: /{indexerId}/api
                if (pathSegments.Length >= 2 && pathSegments[1] == "api")
                {
                    return int.TryParse(pathSegments[0], out indexerId);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "Failed to parse indexer URL: {0}", url);
            }

            return false;
        }

        public override void AddIndexer(IndexerDefinition indexer)
        {
            if (indexer.Protocol != DownloadProtocol.Torrent)
            {
                _logger.Debug("Skipping non-torrent indexer {0}", indexer.Name);
                return;
            }

            var crossSeedIndexer = BuildCrossSeedIndexer(indexer);

            try
            {
                // cross-seed POST now handles upserts automatically - no need for conflict handling
                var addedIndexer = _crossSeedProxy.AddIndexer(crossSeedIndexer, Settings);
                _logger.Info("Added/updated indexer {0} in cross-seed with ID {1}", indexer.Name, addedIndexer.Id);
                
                // Create mapping in Prowlarr's database (like other applications do)
                _appIndexerMapService.Insert(new AppIndexerMap 
                { 
                    AppId = Definition.Id, 
                    IndexerId = indexer.Id, 
                    RemoteIndexerId = addedIndexer.Id,
                    RemoteIndexerName = addedIndexer.Name
                });
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to add indexer {0} to cross-seed", indexer.Name);
                throw;
            }
        }

        private string BuildExpectedUrl(IndexerDefinition indexer)
        {
            var prowlarrUrl = Settings.ProwlarrUrl?.TrimEnd('/') ?? _configFileProvider.UrlBase?.TrimEnd('/') ?? "http://localhost:9696";
            return $"{prowlarrUrl}/{indexer.Id}/api";
        }

        private string BuildExpectedUrl(int indexerId)
        {
            var prowlarrUrl = Settings.ProwlarrUrl?.TrimEnd('/') ?? _configFileProvider.UrlBase?.TrimEnd('/') ?? "http://localhost:9696";
            return $"{prowlarrUrl}/{indexerId}/api";
        }

        public override void UpdateIndexer(IndexerDefinition indexer, bool forceSync = false)
        {
            if (indexer.Protocol != DownloadProtocol.Torrent)
            {
                _logger.Debug("Skipping non-torrent indexer {0}", indexer.Name);
                return;
            }

            var mappings = GetIndexerMappings();
            var mapping = mappings.FirstOrDefault(m => m.IndexerId == indexer.Id);

            if (mapping == null)
            {
                _logger.Debug("No existing mapping found for indexer {0}, adding as new", indexer.Name);
                AddIndexer(indexer);
                return;
            }

            var crossSeedIndexer = BuildCrossSeedIndexer(indexer);
            crossSeedIndexer.Id = mapping.RemoteIndexerId;

            try
            {
                var updatedIndexer = _crossSeedProxy.UpdateIndexer(crossSeedIndexer, Settings);
                _logger.Info("Updated indexer {0} in cross-seed (ID: {1})", indexer.Name, updatedIndexer.Id);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to update indexer {0} in cross-seed", indexer.Name);
                throw;
            }
        }

        public override void RemoveIndexer(int indexerId)
        {
            // Use database mappings directly (like other applications do)
            var appMappings = _appIndexerMapService.GetMappingsForApp(Definition.Id);
            var indexerMapping = appMappings.FirstOrDefault(m => m.IndexerId == indexerId);
            
            if (indexerMapping != null)
            {
                try
                {
                    // Call cross-seed DELETE endpoint - it will soft delete (set active=false) internally
                    // This preserves cache data while making the indexer inactive
                    _crossSeedProxy.RemoveIndexer(indexerMapping.RemoteIndexerId, Settings);
                    _logger.Info("Soft deleted indexer {0} in cross-seed (remote ID: {1}) - cache data preserved", indexerId, indexerMapping.RemoteIndexerId);

                    // Clean up the mapping from Prowlarr's database (like other applications do)
                    _appIndexerMapService.Delete(indexerMapping.Id);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to remove indexer {0} from cross-seed", indexerId);
                    throw;
                }
            }
            else
            {
                _logger.Debug("No mapping found for indexer ID {0}, nothing to remove", indexerId);
            }
        }

        private CrossSeedIndexer BuildCrossSeedIndexer(IndexerDefinition indexer)
        {
            var prowlarrUrl = Settings.ProwlarrUrl?.TrimEnd('/') ?? _configFileProvider.UrlBase?.TrimEnd('/') ?? "http://localhost:9696";

            return new CrossSeedIndexer
            {
                Name = indexer.Name,
                Url = $"{prowlarrUrl}/{indexer.Id}/api",
                ApiKey = _configFileProvider.ApiKey,
                Active = indexer.Enable && (indexer.AppProfile?.Value?.EnableRss ?? true)
            };
        }
    }
}
