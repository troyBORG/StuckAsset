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
	internal const string VERSION_CONSTANT = "1.0.0";
	public override string Name => "StuckAsset";
	public override string Author => "troyBORG";
	public override string Version => VERSION_CONSTANT;
	public override string Link => "https://github.com/troyBORG/StuckAsset/";

	private static StuckAssetMod? instance;
	private CancellationTokenSource? cancellationTokenSource;
	private Task? monitorTask;
	private Dictionary<EngineGatherJob, DateTime> jobStartTimes = new Dictionary<EngineGatherJob, DateTime>();
	private Dictionary<Uri, DateTime> retryQueue = new Dictionary<Uri, DateTime>(); // URLs to retry later
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
	
	// Timeout configuration
	private static readonly float STUCK_JOB_TIMEOUT_SECONDS = 300f; // 5 minutes
	private static readonly float MONITOR_INTERVAL_SECONDS = 10f; // Check every 10 seconds
	private static readonly float SESSION_DOWNLOAD_TIMEOUT_SECONDS = 120f; // 2 minutes for session downloads
	private static readonly float RETRY_DELAY_SECONDS = 60f; // Wait 1 minute before retrying
	private static readonly float RETRY_CHECK_INTERVAL_SECONDS = 30f; // Check retry queue every 30 seconds
	private static readonly float STATS_UPDATE_INTERVAL_SECONDS = 5f; // Update stats every 5 seconds

	public override void OnEngineInit() {
		instance = this;
		
		// Initialize config
		Config = GetConfiguration();
		Config?.Save(true);
		
		Harmony harmony = new("com.troyBORG.StuckAsset");
		harmony.PatchAll();
		
		// Start monitoring tasks
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
				await Task.Delay(TimeSpan.FromSeconds(MONITOR_INTERVAL_SECONDS), cancellationToken);
				
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
				
				if (stuckCount > 0) {
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
		
		// For local:// assets, check if the owner has left the session
		if (job.URL?.Scheme == "local" && job.URL.Host != null) {
			if (!IsUserStillInSession(job.URL.Host)) {
				// Owner has left - fail immediately
				return true;
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
			return elapsed > SESSION_DOWNLOAD_TIMEOUT_SECONDS;
		}
		
		return elapsed > STUCK_JOB_TIMEOUT_SECONDS;
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
			Error($"Error checking if user {machineId} is in session: {ex}");
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
				if (!IsUserStillInSession(job.URL.Host)) {
					reason = "Asset owner left the session (skipped by StuckAsset mod, will retry later)";
					ownerLeft = true;
				}
			}
			
			// Clear the cache for this asset
			ClearAssetCache(job.URL);
			
			// Cancel/remove the job from the queue instead of failing it
			CancelJob(job);
			
			// Add to retry queue (unless owner left - no point retrying those immediately)
			if (!ownerLeft) {
				lock (retryQueueLock) {
					retryQueue[job.URL] = DateTime.UtcNow.AddSeconds(RETRY_DELAY_SECONDS);
				}
				Msg($"Skipped stuck job: {job.URL} - {reason}");
			} else {
				// For owner-left cases, add to retry queue with longer delay
				lock (retryQueueLock) {
					retryQueue[job.URL] = DateTime.UtcNow.AddSeconds(RETRY_DELAY_SECONDS * 2);
				}
				Msg($"Skipped stuck job (owner left): {job.URL} - will retry later");
			}
		} catch (Exception ex) {
			Error($"Error cleaning up stuck job {job.URL}: {ex}");
		}
	}

	private void CancelJob(EngineGatherJob job) {
		try {
			// Try to find a Cancel method
			var cancelMethod = typeof(GatherJob).GetMethod("Cancel", 
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			if (cancelMethod != null) {
				cancelMethod.Invoke(job, null);
				return;
			}
			
			// If no Cancel method, try to fail it with a special flag that might allow retry
			// Or we can just let it fail but clear cache so it can be retried
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
			Warn($"Could not cancel job {job.URL}, may need manual cleanup: {ex}");
		}
	}

	private void ClearAssetCache(Uri assetUrl) {
		try {
			if (Engine.Current?.LocalDB == null) return;
			
			// Try to delete the cached asset record
			_ = Task.Run(async () => {
				try {
					var record = await Engine.Current.LocalDB.TryFetchAssetRecordAsync(assetUrl);
					if (record?.path != null && System.IO.File.Exists(record.path)) {
						try {
							System.IO.File.Delete(record.path);
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
			Warn($"Could not clear cache for {assetUrl}: {ex}");
		}
	}

	private async Task ProcessRetryQueue(CancellationToken cancellationToken) {
		while (!cancellationToken.IsCancellationRequested) {
			try {
				await Task.Delay(TimeSpan.FromSeconds(RETRY_CHECK_INTERVAL_SECONDS), cancellationToken);
				
				if (Engine.Current?.AssetManager == null) continue;
				
				var now = DateTime.UtcNow;
				List<Uri> toRetry = new List<Uri>();
				
				// Find URLs that are ready to retry
				lock (retryQueueLock) {
					var toRemove = new List<Uri>();
					foreach (var kvp in retryQueue) {
						if (now >= kvp.Value) {
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
						// Re-request the asset with low priority so it doesn't block new assets
						_ = Engine.Current.AssetManager.GatherAsset(url, 0.1f);
						totalJobsRetried++;
						Msg($"Retrying previously skipped asset: {url}");
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

	// Patch to monitor and clean up jobs in AssetManager.Update
	[HarmonyPatch(typeof(AssetManager), "Update")]
	class AssetManager_Update_Patch {
		static void Postfix(AssetManager __instance, double assetsMaxMilliseconds, double particlesMaxMilliseconds) {
			try {
				if (instance == null) return;
				
				var gatherer = instance.GetAssetGatherer(__instance);
				if (gatherer == null) return;
				
				var jobs = new List<EngineGatherJob>();
				gatherer.GetActiveJobs(jobs);
				
				var now = DateTime.UtcNow;
				foreach (var job in jobs) {
					if (job != null && instance.IsJobStuck(job, now)) {
						instance.CleanupStuckJob(job);
					}
				}
			} catch (Exception ex) {
				StuckAssetMod.Error($"Error in AssetManager.Update patch: {ex}");
			}
		}
	}
}
