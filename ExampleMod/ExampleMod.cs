using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Elements.Assets;
using Elements.Core;
using FrooxEngine;
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
	private object jobStartTimesLock = new object();
	
	// Configuration
	private static readonly float STUCK_JOB_TIMEOUT_SECONDS = 300f; // 5 minutes
	private static readonly float MONITOR_INTERVAL_SECONDS = 10f; // Check every 10 seconds
	private static readonly float SESSION_DOWNLOAD_TIMEOUT_SECONDS = 120f; // 2 minutes for session downloads

	public override void OnEngineInit() {
		instance = this;
		Harmony harmony = new("com.troyBORG.StuckAsset");
		harmony.PatchAll();
		
		// Start monitoring task
		cancellationTokenSource = new CancellationTokenSource();
		monitorTask = Task.Run(() => MonitorAssetJobs(cancellationTokenSource.Token));
		
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
						CleanupStuckJob(job);
						cleanedCount++;
						
						// Remove from tracking
						lock (jobStartTimesLock) {
							jobStartTimes.Remove(job);
						}
					}
				}
				
				if (stuckCount > 0) {
					Msg($"Detected {stuckCount} stuck asset job(s), cleaned up {cleanedCount}");
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
		try {
			// Determine the reason for cleanup
			string reason = "Job timed out (cleaned up by StuckAsset mod)";
			if (job.URL?.Scheme == "local" && job.URL.Host != null) {
				if (!IsUserStillInSession(job.URL.Host)) {
					reason = "Asset owner left the session (cleaned up by StuckAsset mod)";
				}
			}
			
			// Try to fail the job gracefully using reflection
			var failMethod = typeof(GatherJob).GetMethod("Fail", 
				System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
			if (failMethod != null) {
				failMethod.Invoke(job, new object?[] { 
					reason, 
					true, 
					null, 
					(System.Net.HttpStatusCode)0 
				});
				Msg($"Cleaned up stuck job: {job.URL} - {reason}");
			} else {
				Warn($"Could not find Fail method on GatherJob, job may remain stuck: {job.URL}");
			}
		} catch (Exception ex) {
			Error($"Error cleaning up stuck job {job.URL}: {ex}");
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
