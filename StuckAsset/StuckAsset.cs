using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
using FrooxEngine.Store;
using HarmonyLib;
using ResoniteModLoader;
using SkyFrost.Base;

namespace StuckAsset;
// Mod to fix stuck asset queues in Resonite
public class StuckAssetMod : ResoniteMod {
	internal const string VERSION_CONSTANT = "1.1.0";
	public override string Name => "StuckAsset";
	public override string Author => "troyBORG";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/troyBORG/StuckAsset/";

	private static StuckAssetMod? instance;
	private CancellationTokenSource? cancellationTokenSource;
	private Task? monitorTask;
	private Dictionary<EngineGatherJob, DateTime> jobStartTimes = new Dictionary<EngineGatherJob, DateTime>();
	private Dictionary<Uri, DateTime> retryQueue = new Dictionary<Uri, DateTime>(); // URLs to retry later
	private Dictionary<Uri, int> retryCounts = new Dictionary<Uri, int>(); // Track retry attempts per asset
	private Dictionary<Uri, DateTime> assetCooldowns = new Dictionary<Uri, DateTime>(); // Cooldown tracking
	private object jobStartTimesLock = new object();
	private object retryQueueLock = new object();
	
	// Statistics
	private int totalStuckJobsDetected = 0;
	private int totalJobsSkipped = 0;
	private int totalJobsRetried = 0;
	private int currentRetryQueueSize = 0;
	private int currentActiveJobs = 0;
	
	// Configuration
	private static ModConfiguration? Config;
	
	// Feature toggles
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> enabled = new ModConfigurationKey<bool>(
		"enabled", 
		"Enable the StuckAsset mod", 
		() => true
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> monitorIntervalSeconds = new ModConfigurationKey<float>(
		"monitorIntervalSeconds", 
		"Interval between monitoring checks (seconds)", 
		() => 10f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> remoteTimeoutSeconds = new ModConfigurationKey<float>(
		"remoteTimeoutSeconds", 
		"Timeout for remote asset downloads (seconds)", 
		() => 240f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> localTimeoutSeconds = new ModConfigurationKey<float>(
		"localTimeoutSeconds", 
		"Timeout for local/session asset transfers (seconds)", 
		() => 90f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> retryDelaySeconds = new ModConfigurationKey<float>(
		"retryDelaySeconds", 
		"Delay before retrying a skipped asset (seconds)", 
		() => 45f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> retryDelayOwnerLeftSeconds = new ModConfigurationKey<float>(
		"retryDelayOwnerLeftSeconds", 
		"Delay before retrying when owner left (seconds)", 
		() => 300f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> retryCheckIntervalSeconds = new ModConfigurationKey<float>(
		"retryCheckIntervalSeconds", 
		"Interval for checking retry queue (seconds)", 
		() => 20f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> retryPriority = new ModConfigurationKey<float>(
		"retryPriority", 
		"Priority for retried assets (0.0-1.0, lower = less priority)", 
		() => 0.1f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> clearCacheOnSkip = new ModConfigurationKey<bool>(
		"clearCacheOnSkip", 
		"Clear asset cache when skipping (can cause re-downloads)", 
		() => false
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> cancelJobOnSkip = new ModConfigurationKey<bool>(
		"cancelJobOnSkip", 
		"Cancel jobs when skipping (vs failing them)", 
		() => true
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> maxRetriesPerAsset = new ModConfigurationKey<int>(
		"maxRetriesPerAsset", 
		"Maximum retry attempts per asset before giving up", 
		() => 3
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> maxRetryQueueSize = new ModConfigurationKey<int>(
		"maxRetryQueueSize", 
		"Maximum size of retry queue", 
		() => 250
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<float> cooldownPerAssetSeconds = new ModConfigurationKey<float>(
		"cooldownPerAssetSeconds", 
		"Minimum cooldown before same asset can be retried (seconds)", 
		() => 120f
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> onlyAffectLocalAssets = new ModConfigurationKey<bool>(
		"onlyAffectLocalAssets", 
		"Only process local:// assets (debug mode)", 
		() => false
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> logStuckDetections = new ModConfigurationKey<bool>(
		"logStuckDetections", 
		"Log when stuck jobs are detected", 
		() => true
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> logRetries = new ModConfigurationKey<bool>(
		"logRetries", 
		"Log when assets are retried", 
		() => true
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> logCacheClears = new ModConfigurationKey<bool>(
		"logCacheClears", 
		"Log when cache is cleared", 
		() => false
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> logVerboseDebug = new ModConfigurationKey<bool>(
		"logVerboseDebug", 
		"Enable verbose debug logging", 
		() => false
	);
	
	// Stats (read-only)
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<bool> showStats = new ModConfigurationKey<bool>(
		"showStats", 
		"Show mod statistics in config", 
		() => true
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> statsTotalDetected = new ModConfigurationKey<int>(
		"statsTotalDetected", 
		"Total stuck jobs detected (read-only)", 
		() => 0
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> statsTotalSkipped = new ModConfigurationKey<int>(
		"statsTotalSkipped", 
		"Total jobs skipped (read-only)", 
		() => 0
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> statsTotalRetried = new ModConfigurationKey<int>(
		"statsTotalRetried", 
		"Total jobs retried (read-only)", 
		() => 0
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> statsRetryQueueSize = new ModConfigurationKey<int>(
		"statsRetryQueueSize", 
		"Current retry queue size (read-only)", 
		() => 0
	);
	
	[AutoRegisterConfigKey]
	private static readonly ModConfigurationKey<int> statsActiveJobs = new ModConfigurationKey<int>(
		"statsActiveJobs", 
		"Current active asset jobs (read-only)", 
		() => 0
	);
	
	private static readonly float STATS_UPDATE_INTERVAL_SECONDS = 10f; // Update stats every 10 seconds

	public override void OnEngineInit() {
		instance = this;
		
		// Initialize config
		Config = GetConfiguration();
		Config?.Save(true);
		
		if (!Config?.GetValue(enabled) ?? false) {
			Msg("StuckAsset mod is disabled in config");
			return;
		}
		
		Harmony harmony = new("com.troyBORG.StuckAsset");
		harmony.PatchAll();
		
		// Start monitoring tasks (ONLY background monitor, no Update patch)
		cancellationTokenSource = new CancellationTokenSource();
		monitorTask = Task.Run(() => MonitorAssetJobs(cancellationTokenSource.Token));
		_ = Task.Run(() => ProcessRetryQueue(cancellationTokenSource.Token));
		_ = Task.Run(() => UpdateStats(cancellationTokenSource.Token));
		
		// Register shutdown handler
		Engine.Current.OnShutdown += () => {
			cancellationTokenSource?.Cancel();
			try {
				monitorTask?.Wait(5000);
			} catch (Exception) {
				// Ignore
			}
			cancellationTokenSource?.Dispose();
		};
		
		Msg("StuckAsset mod initialized - monitoring asset gather jobs for stuck states");
	}

	private async Task MonitorAssetJobs(CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			try {
				var interval = Config?.GetValue(monitorIntervalSeconds) ?? 10f;
				await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
				
				if (!Config?.GetValue(enabled) ?? false) continue;
				if (Engine.Current?.AssetManager == null) continue;
				
				var gatherer = GetAssetGatherer(Engine.Current.AssetManager);
				if (gatherer == null) continue;
				
				var jobs = new List<EngineGatherJob>();
				gatherer.GetAllJobs(jobs);
				
				var now = DateTime.UtcNow;
				int stuckCount = 0;
				int cleanedCount = 0;
				
				// Clean up finished/failed jobs from tracking
				lock (jobStartTimesLock) {
					var toRemove = new List<EngineGatherJob>();
					foreach (var kvp in jobStartTimes) {
						if (kvp.Key.State == GatherJobState.Finished || kvp.Key.State == GatherJobState.Failed) {
							toRemove.Add(kvp.Key);
						}
					}
					foreach (var job in toRemove) {
						jobStartTimes.Remove(job);
					}
				}
				
				foreach (var job in jobs) {
					if (job == null) continue;
					
					// Check if we should only process local assets
					if (Config?.GetValue(onlyAffectLocalAssets) ?? false) {
						if (job.URL?.Scheme != "local") continue;
					}
					
					// Track new jobs
					lock (jobStartTimesLock) {
						if (!jobStartTimes.ContainsKey(job) && 
						    job.State != GatherJobState.Finished && 
						    job.State != GatherJobState.Failed) {
							jobStartTimes[job] = now;
						}
					}
					
					// Check if job is stuck
					if (IsJobStuck(job, now)) {
						stuckCount++;
						totalStuckJobsDetected++;
						CleanupStuckJob(job);
						cleanedCount++;
						totalJobsSkipped++;
						
						// Remove from tracking
						lock (jobStartTimesLock) {
							jobStartTimes.Remove(job);
						}
					}
				}
				
				// Update active jobs count
				currentActiveJobs = jobs.Count(j => j != null && 
					j.State != GatherJobState.Finished && 
					j.State != GatherJobState.Failed);
				
				// Update retry queue size
				lock (retryQueueLock) {
					currentRetryQueueSize = retryQueue.Count;
				}
				
				if (stuckCount > 0 && (Config?.GetValue(logStuckDetections) ?? true)) {
					Msg($"Detected {stuckCount} stuck asset job(s), skipped {cleanedCount}");
				}
			} catch (OperationCanceledException) {
				break;
			} catch (Exception ex) {
				Error($"Error in asset job monitor: {ex}");
			}
		}
	}

	private bool IsJobStuck(EngineGatherJob job, DateTime now) {
		if (job.State == GatherJobState.Finished || job.State == GatherJobState.Failed) {
			return false;
		}
		
		// Get timeout values from config
		var localTimeout = Config?.GetValue(localTimeoutSeconds) ?? 90f;
		var remoteTimeout = Config?.GetValue(remoteTimeoutSeconds) ?? 240f;
		
		// For local:// assets, check if the owner has left the session (with fallback)
		bool ownerLeft = false;
		if (job.URL?.Scheme == "local" && job.URL.Host != null) {
			// Try to detect owner leaving, but don't rely on it completely
			try {
				ownerLeft = !IsUserStillInSession(job.URL.Host);
			} catch {
				// If detection fails, assume owner is still there (safer)
				ownerLeft = false;
			}
		}
		
		DateTime startTime;
		lock (jobStartTimesLock) {
			if (!jobStartTimes.TryGetValue(job, out startTime)) {
				// If we don't have a start time, use current time as fallback
				startTime = now;
				jobStartTimes[job] = startTime;
			}
		}
		
		var elapsed = (now - startTime).TotalSeconds;
		
		// Session downloads should timeout faster
		if (job.URL?.Scheme == "local") {
			return elapsed > localTimeout;
		}
		
		return elapsed > remoteTimeout;
	}

	private bool IsUserStillInSession(string machineId) {
		if (Engine.Current?.WorldManager == null) return false;
		
		try {
			var worlds = new List<World>();
			Engine.Current.WorldManager.GetWorlds(worlds);
			
			foreach (var world in worlds) {
				if (world?.Session == null) continue;
				
				// Check if the user is in this world
				var user = world.GetUserByMachineId(machineId);
				if (user != null) {
					return true; // User is still in a session
				}
			}
			
			return false; // User not found in any world
		} catch (Exception ex) {
			if (Config?.GetValue(logVerboseDebug) ?? false) {
				Warn($"Error checking if user {machineId} is in session: {ex}");
			}
			// On error, assume they're still there to avoid false positives
			return true;
		}
	}

	private void CleanupStuckJob(EngineGatherJob job) {
		if (job.URL == null) return;
		
		try {
			// Determine the reason for cleanup
			string reason = "Job timed out (skipped by StuckAsset mod, will retry later)";
			bool ownerLeft = false;
			if (job.URL.Scheme == "local" && job.URL.Host != null) {
				try {
					ownerLeft = !IsUserStillInSession(job.URL.Host);
				} catch {
					ownerLeft = false;
				}
				if (ownerLeft) {
					reason = "Asset owner left the session (skipped by StuckAsset mod, will retry later)";
				}
			}
			
			// Check retry count
			lock (retryQueueLock) {
				if (!retryCounts.TryGetValue(job.URL, out int retryCount)) {
					retryCount = 0;
				}
				
				var maxRetries = Config?.GetValue(maxRetriesPerAsset) ?? 3;
				if (retryCount >= maxRetries) {
					// Too many retries, give up permanently
					if (Config?.GetValue(logStuckDetections) ?? true) {
						Warn($"Asset {job.URL} exceeded max retries ({maxRetries}), giving up");
					}
					CancelJob(job);
					return;
				}
			}
			
			// Check cooldown
			lock (retryQueueLock) {
				if (assetCooldowns.TryGetValue(job.URL, out DateTime cooldownUntil)) {
					if (DateTime.UtcNow < cooldownUntil) {
						// Still in cooldown, just cancel and skip
						CancelJob(job);
						return;
					}
				}
			}
			
			// Clear cache based on retry count and config
			bool shouldClearCache = Config?.GetValue(clearCacheOnSkip) ?? false;
			lock (retryQueueLock) {
				if (retryCounts.TryGetValue(job.URL, out int count)) {
					// Clear cache after 2nd retry attempt
					if (count >= 2) {
						shouldClearCache = true;
					}
				}
			}
			
			if (shouldClearCache) {
				ClearAssetCache(job.URL);
			}
			
			// Cancel/remove the job from the queue
			CancelJob(job);
			
			// Check retry queue size limit
			lock (retryQueueLock) {
				var maxQueueSize = Config?.GetValue(maxRetryQueueSize) ?? 250;
				if (retryQueue.Count >= maxQueueSize) {
					if (Config?.GetValue(logStuckDetections) ?? true) {
						Warn($"Retry queue full ({maxQueueSize}), skipping retry for {job.URL}");
					}
					return;
				}
				
				// Add to retry queue
				var retryDelay = ownerLeft 
					? (Config?.GetValue(retryDelayOwnerLeftSeconds) ?? 300f)
					: (Config?.GetValue(retryDelaySeconds) ?? 45f);
				
				retryQueue[job.URL] = DateTime.UtcNow.AddSeconds(retryDelay);
				
				// Set cooldown
				var cooldown = Config?.GetValue(cooldownPerAssetSeconds) ?? 120f;
				assetCooldowns[job.URL] = DateTime.UtcNow.AddSeconds(cooldown);
				
				if (Config?.GetValue(logStuckDetections) ?? true) {
					Msg($"Skipped stuck job: {job.URL} - {reason}");
				}
			}
		} catch (Exception ex) {
			Error($"Error cleaning up stuck job {job.URL}: {ex}");
		}
	}

	private void CancelJob(EngineGatherJob job) {
		try {
			var shouldCancel = Config?.GetValue(cancelJobOnSkip) ?? true;
			
			if (shouldCancel) {
				// Try to find a Cancel method
				var cancelMethod = typeof(GatherJob).GetMethod("Cancel", 
					System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
				if (cancelMethod != null) {
					cancelMethod.Invoke(job, null);
					return;
				}
			}
			
			// If no Cancel method or cancelJobOnSkip is false, try to fail it
			var failMethod = typeof(GatherJob).GetMethod("Fail", 
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			if (failMethod != null) {
				// Fail with a reason that indicates it should be retryable
				failMethod.Invoke(job, new object?[] { 
					"Skipped for retry", 
					false, // Don't mark as permanent failure
					null, 
					(System.Net.HttpStatusCode)0 
				});
			}
		} catch (Exception ex) {
			if (Config?.GetValue(logVerboseDebug) ?? false) {
				Warn($"Could not cancel job {job.URL}, may need manual cleanup: {ex}");
			}
		}
	}

	private void ClearAssetCache(Uri assetUrl) {
		try {
			if (Engine.Current?.LocalDB == null) return;
			
			if (Config?.GetValue(logCacheClears) ?? false) {
				Msg($"Clearing cache for {assetUrl}");
			}
			
			// Try to get the record and delete the file if it exists
			_ = Task.Run(async () => {
				try {
					var record = await Engine.Current.LocalDB.TryFetchAssetRecordAsync(assetUrl);
					if (record?.path != null && System.IO.File.Exists(record.path)) {
						try {
							System.IO.File.Delete(record.path);
							if (Config?.GetValue(logCacheClears) ?? false) {
								Msg($"Deleted cached file: {record.path}");
							}
						} catch {
							// Ignore file deletion errors
						}
					}
					// Try to delete the record from the database
					var deleteMethod = typeof(LocalDB).GetMethod("DeleteAssetRecordAsync", 
						System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
					if (deleteMethod != null) {
						var task = deleteMethod.Invoke(Engine.Current.LocalDB, new object[] { assetUrl });
						if (task is Task deleteTask) {
							await deleteTask;
						}
					}
				} catch {
					// Ignore errors - cache might not exist
				}
			});
		} catch (Exception ex) {
			if (Config?.GetValue(logVerboseDebug) ?? false) {
				Warn($"Could not clear cache for {assetUrl}: {ex}");
			}
		}
	}

	private async Task ProcessRetryQueue(CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			try {
				var interval = Config?.GetValue(retryCheckIntervalSeconds) ?? 20f;
				await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
				
				if (!Config?.GetValue(enabled) ?? false) continue;
				if (Engine.Current?.AssetManager == null) continue;
				
				var now = DateTime.UtcNow;
				List<Uri> toRetry = new List<Uri>();
				
				// Find URLs that are ready to retry
				lock (retryQueueLock) {
					var toRemove = new List<Uri>();
					foreach (var kvp in retryQueue) {
						if (now >= kvp.Value) {
							// Check cooldown
							if (assetCooldowns.TryGetValue(kvp.Key, out DateTime cooldownUntil)) {
								if (now < cooldownUntil) {
									continue; // Still in cooldown
								}
							}
							
							// Check retry count
							if (!retryCounts.TryGetValue(kvp.Key, out int count)) {
								count = 0;
							}
							var maxRetries = Config?.GetValue(maxRetriesPerAsset) ?? 3;
							if (count >= maxRetries) {
								toRemove.Add(kvp.Key);
								continue; // Exceeded max retries
							}
							
							toRetry.Add(kvp.Key);
							toRemove.Add(kvp.Key);
						}
					}
					foreach (var url in toRemove) {
						retryQueue.Remove(url);
					}
				}
				
				// Retry the assets
				foreach (var url in toRetry) {
					try {
						// Increment retry count
						lock (retryQueueLock) {
							if (!retryCounts.TryGetValue(url, out int count)) {
								count = 0;
							}
							retryCounts[url] = count + 1;
						}
						
						// Re-request the asset with configured priority
						var priority = Config?.GetValue(retryPriority) ?? 0.1f;
						_ = Engine.Current.AssetManager.GatherAsset(url, priority);
						totalJobsRetried++;
						
						if (Config?.GetValue(logRetries) ?? true) {
							lock (retryQueueLock) {
								var count = retryCounts.TryGetValue(url, out int c) ? c : 0;
								Msg($"Retrying previously skipped asset (attempt {count}): {url}");
							}
						}
					} catch (Exception ex) {
						Warn($"Error retrying asset {url}: {ex}");
					}
				}
			} catch (OperationCanceledException) {
				break;
			} catch (Exception ex) {
				Error($"Error in retry queue processor: {ex}");
			}
		}
	}

	private async Task UpdateStats(CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			try {
				await Task.Delay(TimeSpan.FromSeconds(STATS_UPDATE_INTERVAL_SECONDS), cancellationToken);
				
				if (Config == null || !Config.GetValue(showStats)) continue;
				
				// Update config values with current statistics
				var config = Config;
				if (config == null) continue;
				
				config.Set(showStats, true);
				config.Set(statsTotalDetected, totalStuckJobsDetected);
				config.Set(statsTotalSkipped, totalJobsSkipped);
				config.Set(statsTotalRetried, totalJobsRetried);
				config.Set(statsRetryQueueSize, currentRetryQueueSize);
				config.Set(statsActiveJobs, currentActiveJobs);
			} catch (OperationCanceledException) {
				break;
			} catch (Exception ex) {
				Error($"Error updating stats: {ex}");
			}
		}
	}

	private EngineAssetGatherer? GetAssetGatherer(AssetManager manager) {
		var field = typeof(AssetManager).GetField("assetGatherer", 
			System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
		return field?.GetValue(manager) as EngineAssetGatherer;
	}
}
