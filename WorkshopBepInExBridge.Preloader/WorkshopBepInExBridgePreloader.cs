using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;

namespace OstranautsWorkshopBepInExBridge.Preloader
{
	public static class WorkshopBepInExBridgePreloader
	{
		private const string AppId = "1022980";
		private const string ManagedFolderName = "Workshop";
		private const string ManifestFileName = "OstranautsWorkshopBepInExBridge.manifest.tsv";
		private const string ConfigFileName = "com.ostranauts.workshop.bepinexbridge.cfg";

		private static bool _ran;

		public static IEnumerable<string> TargetDLLs
		{
			get
			{
				return new[] { "Assembly-CSharp.dll" };
			}
		}

		public static void Initialize()
		{
			RunOnce();
		}

		public static void Patch(AssemblyDefinition assembly)
		{
			RunOnce();
		}

		private static void RunOnce()
		{
			if (_ran)
			{
				return;
			}

			_ran = true;
			try
			{
				BridgePaths selfPaths = BridgePaths.FromPreloaderAssembly();
				BridgeConfig selfConfig = BridgeConfig.Load(Path.Combine(selfPaths.ConfigPath, ConfigFileName));
				if (TrySelfUpdate(selfPaths, selfConfig))
				{
					return; // relaunch + exit already requested; never reached
				}

				SyncResult result = SyncWorkshopPayloads();
				Log("scan complete. Mode=" + result.Mode + ", items=" + result.ItemsScanned + ", copied=" + result.Copied + ", skipped=" + result.Skipped + ", removed=" + result.Removed + ".");
			}
			catch (Exception ex)
			{
				Log("failed: " + ex);
			}
		}

		// Replaces the bridge's own installed DLLs when a newer copy arrives in its
		// Workshop folder, then relaunches the game. Runs in the preloader (before
		// BepInEx loads the plugin assembly): the plugin DLL isn't locked yet, and
		// Windows allows renaming this loaded preloader DLL within the same volume.
		private static bool TrySelfUpdate(BridgePaths paths, BridgeConfig config)
		{
			if (!config.SelfUpdate)
			{
				return false;
			}

			string preloaderInstalled = NormalizeFullPath(Assembly.GetExecutingAssembly().Location);
			string pluginInstalled = NormalizeFullPath(Path.Combine(paths.PluginPath, "OstranautsWorkshopBepInExBridge.dll"));

			// Clear stale *.old from a prior update; they are no longer mapped now.
			TryDeleteOld(preloaderInstalled);
			TryDeleteOld(pluginInstalled);

			// Scan all Workshop items regardless of loading_order — the bridge item
			// is a BepInEx loader and may not appear in the game's mod load order.
			string preloaderSrc = FindBridgeSource(paths, config, "patchers", "OstranautsWorkshopBepInExBridge.Preloader.dll");
			string pluginSrc = FindBridgeSource(paths, config, "plugins", "OstranautsWorkshopBepInExBridge.dll");

			bool staged = false;
			staged |= StageIfNewer(preloaderSrc, preloaderInstalled);
			staged |= StageIfNewer(pluginSrc, pluginInstalled);
			if (!staged)
			{
				return false;
			}

			Log("Self-update staged. Relaunching Ostranauts.");
			RelaunchAndExit(); // does not return
			return true;
		}

		private static string FindBridgeSource(BridgePaths paths, BridgeConfig config, string payloadName, string dllName)
		{
			string newest = null;
			DateTime newestTime = DateTime.MinValue;
			foreach (string itemDir in GetAllWorkshopItemDirs(paths, config))
			{
				string candidate = Path.Combine(itemDir, "BepInEx", payloadName, dllName);
				if (!File.Exists(candidate))
				{
					continue;
				}

				DateTime time = File.GetLastWriteTimeUtc(candidate);
				if (newest == null || time > newestTime)
				{
					newest = NormalizeFullPath(candidate);
					newestTime = time;
				}
			}

			return newest;
		}

		private static bool StageIfNewer(string source, string installed)
		{
			if (source == null || !File.Exists(installed) || FilesAlreadyMatch(source, installed))
			{
				return false;
			}

			// ponytail: size+mtime match is the re-trigger guard (we stamp the mtime
			// below). If a same-size rebuild keeps an identical mtime it won't update;
			// if mtime rounding ever flaps, upgrade to a "last applied source ticks"
			// marker in config/.
			File.Move(installed, installed + ".old");
			File.Copy(source, installed);
			File.SetLastWriteTimeUtc(installed, File.GetLastWriteTimeUtc(source));
			Log("Updated " + Path.GetFileName(installed) + " from " + source);
			return true;
		}

		private static void TryDeleteOld(string path)
		{
			string old = path + ".old";
			if (!File.Exists(old))
			{
				return;
			}

			try
			{
				File.Delete(old);
			}
			catch (Exception ex)
			{
				Log("Could not delete " + old + " (will retry next start): " + ex.Message);
			}
		}

		private static void RelaunchAndExit()
		{
			// Steam dedups by app-id, so it won't relaunch while this process is alive.
			// Spawn a detached helper that waits for our PID to exit, then asks Steam to
			// relaunch. ponytail: relies on Steam being the launcher (it is, for a
			// Workshop user); add a game-exe fallback only if launching outside Steam is reported.
			int pid = Process.GetCurrentProcess().Id;
			ProcessStartInfo psi = new ProcessStartInfo("powershell.exe",
				"-NoProfile -WindowStyle Hidden -Command \"" +
				"Wait-Process -Id " + pid + " -ErrorAction SilentlyContinue; " +
				"Start-Process 'steam://rungameid/" + AppId + "'\"")
			{
				UseShellExecute = false,
				CreateNoWindow = true
			};
			Process.Start(psi);
			Environment.Exit(0);
		}

		private static SyncResult SyncWorkshopPayloads()
		{
			BridgePaths paths = BridgePaths.FromPreloaderAssembly();
			BridgeConfig config = BridgeConfig.Load(Path.Combine(paths.ConfigPath, ConfigFileName));
			string manifestPath = Path.Combine(paths.ConfigPath, ManifestFileName);
			List<ManifestEntry> previousEntries = LoadManifest(manifestPath);
			Dictionary<string, ManifestEntry> previousByDest = previousEntries
				.GroupBy(e => e.DestinationRelative, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
			List<CopySpec> specs = new List<CopySpec>();
			SyncResult result = new SyncResult();

			List<string> itemDirs;
			if (TryGetLoadingOrderWorkshopItemDirs(paths, config, out itemDirs))
			{
				result.Mode = "loading_order";
			}
			else if (config.FallbackToWorkshopFolderScan)
			{
				result.Mode = "folder_scan";
				itemDirs = GetAllWorkshopItemDirs(paths, config).ToList();
			}
			else
			{
				result.Mode = "loading_order_missing";
				Log("No loading_order.json was found. Skipping Workshop BepInEx preloader sync and cleanup.");
				return result;
			}

			foreach (string itemDir in itemDirs)
			{
				string workshopId = Path.GetFileName(itemDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
				string sourceBepInEx = Path.Combine(itemDir, "BepInEx");
				if (!Directory.Exists(sourceBepInEx))
				{
					continue;
				}

				result.ItemsScanned++;
				AddPayloadSpecs(specs, paths, workshopId, sourceBepInEx, "plugins", Path.Combine(paths.PluginPath, ManagedFolderName, workshopId));
				if (config.SyncPatchers)
				{
					AddPayloadSpecs(specs, paths, workshopId, sourceBepInEx, "patchers", Path.Combine(paths.PatcherPath, ManagedFolderName, workshopId));
				}
				if (config.SyncConfig)
				{
					string configDest = config.CopyConfigToRoot ? paths.ConfigPath : Path.Combine(paths.ConfigPath, ManagedFolderName, workshopId);
					AddPayloadSpecs(specs, paths, workshopId, sourceBepInEx, "config", configDest);
				}
			}

			HashSet<string> currentDestinations = new HashSet<string>(specs.Select(s => s.DestinationRelative), StringComparer.OrdinalIgnoreCase);
			List<ManifestEntry> nextManifest = new List<ManifestEntry>();
			if (config.RemoveOrphanedManagedFiles)
			{
				foreach (ManifestEntry entry in previousEntries)
				{
					if (!currentDestinations.Contains(entry.DestinationRelative))
					{
						if (TryRemoveManagedFile(paths.BepInExRoot, entry))
						{
							result.Removed++;
						}
						else
						{
							nextManifest.Add(entry);
						}
					}
				}
			}

			HashSet<string> plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (CopySpec spec in specs.OrderBy(s => s.WorkshopId, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.DestinationRelative, StringComparer.OrdinalIgnoreCase))
			{
				if (!plannedDestinations.Add(spec.DestinationRelative))
				{
					Log("Skipped duplicate destination from Workshop item " + spec.WorkshopId + ": " + spec.DestinationRelative);
					result.Skipped++;
					continue;
				}

				ManifestEntry previous;
				bool wasManaged = previousByDest.TryGetValue(spec.DestinationRelative, out previous);
				if (!CanWriteDestination(config, spec, wasManaged))
				{
					result.Skipped++;
					continue;
				}

				Directory.CreateDirectory(Path.GetDirectoryName(spec.DestinationFull));
				File.Copy(spec.SourceFull, spec.DestinationFull, true);
				File.SetLastWriteTimeUtc(spec.DestinationFull, File.GetLastWriteTimeUtc(spec.SourceFull));
				nextManifest.Add(ManifestEntry.FromSpec(spec));
				result.Copied++;
			}

			SaveManifest(manifestPath, nextManifest);
			RemoveEmptyDirectories(Path.Combine(paths.PluginPath, ManagedFolderName));
			RemoveEmptyDirectories(Path.Combine(paths.PatcherPath, ManagedFolderName));
			if (!config.CopyConfigToRoot)
			{
				RemoveEmptyDirectories(Path.Combine(paths.ConfigPath, ManagedFolderName));
			}

			return result;
		}

		private static void AddPayloadSpecs(List<CopySpec> specs, BridgePaths paths, string workshopId, string sourceBepInEx, string payloadName, string destinationRoot)
		{
			string sourceRoot = Path.Combine(sourceBepInEx, payloadName);
			if (!Directory.Exists(sourceRoot))
			{
				return;
			}

			foreach (string sourceFile in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
			{
				if (ShouldSkipSourceFile(sourceFile))
				{
					continue;
				}

				string relativeToPayload = MakeRelativePath(sourceRoot, sourceFile);
				string destinationFull = NormalizeFullPath(Path.Combine(destinationRoot, relativeToPayload));
				if (!IsInsideDirectory(destinationFull, paths.BepInExRoot))
				{
					Log("Skipped unsafe destination path: " + destinationFull);
					continue;
				}

				specs.Add(new CopySpec
				{
					WorkshopId = workshopId,
					SourceFull = NormalizeFullPath(sourceFile),
					DestinationFull = destinationFull,
					SourceRelative = NormalizeSlashes(Path.Combine(workshopId, "BepInEx", payloadName, relativeToPayload)),
					DestinationRelative = MakeRelativePath(paths.BepInExRoot, destinationFull)
				});
			}
		}

		private static bool ShouldSkipSourceFile(string sourceFile)
		{
			string fileName = Path.GetFileName(sourceFile);
			string assemblyName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
			return string.Equals(fileName, assemblyName, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(fileName, "OstranautsWorkshopBepInExBridge.dll", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(fileName, "OstranautsWorkshopBepInExBridge.Preloader.dll", StringComparison.OrdinalIgnoreCase);
		}

		private static bool CanWriteDestination(BridgeConfig config, CopySpec spec, bool wasManaged)
		{
			if (!File.Exists(spec.DestinationFull) || wasManaged || FilesAlreadyMatch(spec.SourceFull, spec.DestinationFull))
			{
				return true;
			}

			if (config.OverwriteUnmanagedFiles)
			{
				Log("Overwriting unmanaged file: " + spec.DestinationRelative);
				return true;
			}

			Log("Skipped unmanaged existing file: " + spec.DestinationRelative);
			return false;
		}

		private static bool TryRemoveManagedFile(string bepinexRoot, ManifestEntry entry)
		{
			string destination = NormalizeFullPath(Path.Combine(bepinexRoot, entry.DestinationRelative));
			if (!IsInsideDirectory(destination, bepinexRoot) || !File.Exists(destination))
			{
				return false;
			}

			FileInfo fileInfo = new FileInfo(destination);
			if (fileInfo.Length != entry.Length || fileInfo.LastWriteTimeUtc.Ticks != entry.LastWriteTicksUtc)
			{
				Log("Kept modified managed file: " + entry.DestinationRelative);
				return false;
			}

			File.Delete(destination);
			return true;
		}

		private static bool TryGetLoadingOrderWorkshopItemDirs(BridgePaths paths, BridgeConfig config, out List<string> itemDirs)
		{
			itemDirs = new List<string>();
			string loadingOrderPath = GetLoadingOrderPath(paths, config);
			if (string.IsNullOrEmpty(loadingOrderPath))
			{
				return false;
			}

			Log("Using loading order: " + loadingOrderPath);
			HashSet<string> deduped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> workshopRoots = new HashSet<string>(GetWorkshopRoots(paths, config).Select(NormalizeFullPath), StringComparer.OrdinalIgnoreCase);
			foreach (string entry in ReadLoadingOrderEntries(loadingOrderPath))
			{
				string modPath;
				if (!TryGetEnabledModPath(entry, out modPath) || string.IsNullOrWhiteSpace(modPath) || string.Equals(modPath, "core", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (!Path.IsPathRooted(modPath))
				{
					continue;
				}

				string normalized = NormalizeFullPath(modPath);
				if (IsWorkshopItemPath(normalized, workshopRoots))
				{
					deduped.Add(normalized);
				}
			}

			itemDirs.AddRange(deduped.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
			return true;
		}

		private static IEnumerable<string> GetAllWorkshopItemDirs(BridgePaths paths, BridgeConfig config)
		{
			foreach (string workshopRoot in GetWorkshopRoots(paths, config))
			{
				if (!Directory.Exists(workshopRoot))
				{
					continue;
				}

				foreach (string itemDir in Directory.GetDirectories(workshopRoot))
				{
					yield return NormalizeFullPath(itemDir);
				}
			}
		}

		private static string GetLoadingOrderPath(BridgePaths paths, BridgeConfig config)
		{
			foreach (string candidate in GetLoadingOrderPathCandidates(paths, config))
			{
				if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
				{
					return NormalizeFullPath(candidate);
				}
			}

			return null;
		}

		private static IEnumerable<string> GetLoadingOrderPathCandidates(BridgePaths paths, BridgeConfig config)
		{
			if (!string.IsNullOrWhiteSpace(config.LoadingOrderPathOverride))
			{
				yield return config.LoadingOrderPathOverride;
			}

			yield return Path.Combine(paths.GameRoot, "Ostranauts_Data", "Mods", "loading_order.json");
		}

		private static IEnumerable<string> ReadLoadingOrderEntries(string loadingOrderPath)
		{
			string text = File.ReadAllText(loadingOrderPath);
			Match match = Regex.Match(text, "\"aLoadOrder\"\\s*:\\s*\\[(?<items>[\\s\\S]*?)\\]");
			if (!match.Success)
			{
				yield break;
			}

			foreach (Match itemMatch in Regex.Matches(match.Groups["items"].Value, "\"(?<value>(?:\\\\.|[^\"])*)\""))
			{
				yield return UnescapeJsonString(itemMatch.Groups["value"].Value);
			}
		}

		private static bool TryGetEnabledModPath(string loadOrderEntry, out string modPath)
		{
			modPath = null;
			string[] parts = loadOrderEntry.Split('|');
			if (parts.Length > 2 || (parts.Length == 2 && string.Equals(parts[1], "disabled", StringComparison.OrdinalIgnoreCase)))
			{
				return false;
			}

			modPath = parts[0];
			return true;
		}

		private static bool IsWorkshopItemPath(string itemPath, HashSet<string> workshopRoots)
		{
			foreach (string root in workshopRoots)
			{
				if (IsInsideDirectory(itemPath, root))
				{
					return true;
				}
			}

			return false;
		}

		private static IEnumerable<string> GetWorkshopRoots(BridgePaths paths, BridgeConfig config)
		{
			HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrWhiteSpace(config.WorkshopRootOverride))
			{
				roots.Add(NormalizeFullPath(config.WorkshopRootOverride));
			}

			DirectoryInfo gameDir = new DirectoryInfo(paths.GameRoot);
			DirectoryInfo steamApps = gameDir.Parent != null ? gameDir.Parent.Parent : null;
			if (steamApps != null && string.Equals(steamApps.Name, "steamapps", StringComparison.OrdinalIgnoreCase))
			{
				roots.Add(Path.Combine(steamApps.FullName, "workshop", "content", AppId));
				foreach (string libraryPath in ReadSteamLibraryFolders(steamApps.FullName))
				{
					roots.Add(Path.Combine(libraryPath, "steamapps", "workshop", "content", AppId));
				}
			}

			return roots;
		}

		private static IEnumerable<string> ReadSteamLibraryFolders(string steamAppsPath)
		{
			string libraryFoldersPath = Path.Combine(steamAppsPath, "libraryfolders.vdf");
			if (!File.Exists(libraryFoldersPath))
			{
				yield break;
			}

			string text = File.ReadAllText(libraryFoldersPath);
			foreach (Match match in Regex.Matches(text, "\"path\"\\s+\"(?<path>[^\"]+)\""))
			{
				yield return UnescapeVdfPath(match.Groups["path"].Value);
			}

			foreach (Match match in Regex.Matches(text, "\"\\d+\"\\s+\"(?<path>[^\"]+)\""))
			{
				yield return UnescapeVdfPath(match.Groups["path"].Value);
			}
		}

		private static List<ManifestEntry> LoadManifest(string manifestPath)
		{
			List<ManifestEntry> entries = new List<ManifestEntry>();
			if (!File.Exists(manifestPath))
			{
				return entries;
			}

			foreach (string line in File.ReadAllLines(manifestPath))
			{
				ManifestEntry entry;
				if (ManifestEntry.TryParse(line, out entry))
				{
					entries.Add(entry);
				}
			}

			return entries;
		}

		private static void SaveManifest(string manifestPath, IEnumerable<ManifestEntry> entries)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
			File.WriteAllLines(manifestPath, entries.Select(e => e.ToLine()).ToArray());
		}

		private static void RemoveEmptyDirectories(string root)
		{
			if (!Directory.Exists(root))
			{
				return;
			}

			foreach (string directory in Directory.GetDirectories(root))
			{
				RemoveEmptyDirectories(directory);
			}

			if (!Directory.EnumerateFileSystemEntries(root).Any())
			{
				Directory.Delete(root);
			}
		}

		private static bool FilesAlreadyMatch(string source, string destination)
		{
			FileInfo sourceInfo = new FileInfo(source);
			FileInfo destinationInfo = new FileInfo(destination);
			return sourceInfo.Length == destinationInfo.Length && sourceInfo.LastWriteTimeUtc == destinationInfo.LastWriteTimeUtc;
		}

		private static string MakeRelativePath(string root, string fullPath)
		{
			Uri rootUri = new Uri(AppendDirectorySeparator(NormalizeFullPath(root)));
			Uri fileUri = new Uri(NormalizeFullPath(fullPath));
			return NormalizeSlashes(Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()));
		}

		private static string AppendDirectorySeparator(string path)
		{
			if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
			{
				return path;
			}

			return path + Path.DirectorySeparatorChar;
		}

		private static string NormalizeFullPath(string path)
		{
			return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
		}

		private static string NormalizeSlashes(string path)
		{
			return path.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
		}

		private static bool IsInsideDirectory(string fullPath, string root)
		{
			string normalizedRoot = AppendDirectorySeparator(NormalizeFullPath(root));
			string normalizedPath = NormalizeFullPath(fullPath);
			return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
		}

		private static string UnescapeVdfPath(string path)
		{
			return path.Replace("\\\\", "\\");
		}

		private static string UnescapeJsonString(string value)
		{
			return Regex.Replace(value, "\\\\(?:[\"\\\\/bfnrt]|u[0-9a-fA-F]{4})", match =>
			{
				string escaped = match.Value;
				if (escaped.Length == 2)
				{
					switch (escaped[1])
					{
						case '"':
							return "\"";
						case '\\':
							return "\\";
						case '/':
							return "/";
						case 'b':
							return "\b";
						case 'f':
							return "\f";
						case 'n':
							return "\n";
						case 'r':
							return "\r";
						case 't':
							return "\t";
					}
				}

				if (escaped.Length == 6 && escaped[1] == 'u')
				{
					return ((char)Convert.ToInt32(escaped.Substring(2), 16)).ToString();
				}

				return escaped;
			});
		}

		private static void Log(string message)
		{
			Console.WriteLine("[Ostranauts Workshop BepInEx Bridge Preloader] " + message);
		}

		private sealed class BridgePaths
		{
			public string BepInExRoot;
			public string GameRoot;
			public string PluginPath;
			public string PatcherPath;
			public string ConfigPath;

			public static BridgePaths FromPreloaderAssembly()
			{
				string assemblyPath = Assembly.GetExecutingAssembly().Location;
				string patcherPath = Path.GetDirectoryName(assemblyPath);
				string bepinexRoot = Directory.GetParent(patcherPath).FullName;
				return new BridgePaths
				{
					BepInExRoot = NormalizeFullPath(bepinexRoot),
					GameRoot = NormalizeFullPath(Directory.GetParent(bepinexRoot).FullName),
					PluginPath = NormalizeFullPath(Path.Combine(bepinexRoot, "plugins")),
					PatcherPath = NormalizeFullPath(Path.Combine(bepinexRoot, "patchers")),
					ConfigPath = NormalizeFullPath(Path.Combine(bepinexRoot, "config"))
				};
			}
		}

		private sealed class BridgeConfig
		{
			public string WorkshopRootOverride;
			public string LoadingOrderPathOverride;
			public bool FallbackToWorkshopFolderScan;
			public bool SyncPatchers = true;
			public bool SyncConfig = true;
			public bool CopyConfigToRoot = true;
			public bool SelfUpdate = true;
			public bool OverwriteUnmanagedFiles;
			public bool RemoveOrphanedManagedFiles = true;

			public static BridgeConfig Load(string configPath)
			{
				BridgeConfig config = new BridgeConfig();
				if (!File.Exists(configPath))
				{
					return config;
				}

				Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
				string currentSection = "";
				foreach (string rawLine in File.ReadAllLines(configPath))
				{
					string line = rawLine.Trim();
					if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line.StartsWith(";", StringComparison.Ordinal))
					{
						continue;
					}

					if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
					{
						currentSection = line.Substring(1, line.Length - 2).Trim();
						continue;
					}

					int equals = line.IndexOf('=');
					if (equals < 0)
					{
						continue;
					}

					string key = currentSection + "." + line.Substring(0, equals).Trim();
					values[key] = line.Substring(equals + 1).Trim();
				}

				config.WorkshopRootOverride = GetString(values, "Paths.WorkshopRootOverride", "");
				config.LoadingOrderPathOverride = GetString(values, "Paths.LoadingOrderPathOverride", "");
				config.FallbackToWorkshopFolderScan = GetBool(values, "Sync.FallbackToWorkshopFolderScan", false);
				config.SyncPatchers = GetBool(values, "Sync.SyncPatchers", true);
				config.SyncConfig = GetBool(values, "Sync.SyncConfig", true);
				config.CopyConfigToRoot = GetBool(values, "Sync.CopyConfigToRoot", true);
				config.SelfUpdate = GetBool(values, "Sync.SelfUpdate", true);
				config.OverwriteUnmanagedFiles = GetBool(values, "Safety.OverwriteUnmanagedFiles", false);
				config.RemoveOrphanedManagedFiles = GetBool(values, "Safety.RemoveOrphanedManagedFiles", true);
				return config;
			}

			private static string GetString(Dictionary<string, string> values, string key, string defaultValue)
			{
				string value;
				return values.TryGetValue(key, out value) ? value : defaultValue;
			}

			private static bool GetBool(Dictionary<string, string> values, string key, bool defaultValue)
			{
				string value;
				return values.TryGetValue(key, out value) && bool.TryParse(value, out bool parsed) ? parsed : defaultValue;
			}
		}

		private sealed class CopySpec
		{
			public string WorkshopId;
			public string SourceFull;
			public string SourceRelative;
			public string DestinationFull;
			public string DestinationRelative;
		}

		private sealed class ManifestEntry
		{
			public string SourceRelative;
			public string DestinationRelative;
			public long LastWriteTicksUtc;
			public long Length;

			public static ManifestEntry FromSpec(CopySpec spec)
			{
				FileInfo info = new FileInfo(spec.SourceFull);
				return new ManifestEntry
				{
					SourceRelative = spec.SourceRelative,
					DestinationRelative = spec.DestinationRelative,
					LastWriteTicksUtc = info.LastWriteTimeUtc.Ticks,
					Length = info.Length
				};
			}

			public static bool TryParse(string line, out ManifestEntry entry)
			{
				entry = null;
				string[] parts = line.Split('\t');
				if (parts.Length != 4)
				{
					return false;
				}

				long ticks;
				long length;
				if (!long.TryParse(parts[2], out ticks) || !long.TryParse(parts[3], out length))
				{
					return false;
				}

				entry = new ManifestEntry
				{
					SourceRelative = parts[0],
					DestinationRelative = parts[1],
					LastWriteTicksUtc = ticks,
					Length = length
				};
				return true;
			}

			public string ToLine()
			{
				return SourceRelative + "\t" + DestinationRelative + "\t" + LastWriteTicksUtc + "\t" + Length;
			}
		}

		private sealed class SyncResult
		{
			public string Mode = "unknown";
			public int ItemsScanned;
			public int Copied;
			public int Skipped;
			public int Removed;
		}
	}
}
