using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace OstranautsWorkshopBepInExBridge
{
	[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
	public sealed class WorkshopBepInExBridge : BaseUnityPlugin
	{
		private const string PluginGuid = "com.ostranauts.workshop.bepinexbridge";
		private const string PluginName = "Ostranauts Workshop BepInEx Bridge";
		private const string PluginVersion = "0.1.0";
		private const string AppId = "1022980";
		private const string ManagedFolderName = "Workshop";
		private const string ManifestFileName = "OstranautsWorkshopBepInExBridge.manifest.tsv";

		private ConfigEntry<string> _workshopRootOverride;
		private ConfigEntry<string> _loadingOrderPathOverride;
		private ConfigEntry<bool> _fallbackToWorkshopFolderScan;
		private ConfigEntry<bool> _syncPatchers;
		private ConfigEntry<bool> _syncConfig;
		private ConfigEntry<bool> _copyConfigToRoot;
		private ConfigEntry<bool> _overwriteUnmanagedFiles;
		private ConfigEntry<bool> _removeOrphanedFiles;

		private void Awake()
		{
			_workshopRootOverride = Config.Bind("Paths", "WorkshopRootOverride", "", "Optional full path to steamapps/workshop/content/1022980. Leave blank to auto-detect.");
			_loadingOrderPathOverride = Config.Bind("Paths", "LoadingOrderPathOverride", "", "Optional full path to Ostranauts loading_order.json. Leave blank to auto-detect from game settings and default mod folder.");
			_fallbackToWorkshopFolderScan = Config.Bind("Sync", "FallbackToWorkshopFolderScan", false, "If loading_order.json cannot be found, scan every Workshop folder. Off by default so stale folders from unsubscribed mods are not synced.");
			_syncPatchers = Config.Bind("Sync", "SyncPatchers", true, "Copy Workshop BepInEx/patchers content. Patcher changes require a game restart.");
			_syncConfig = Config.Bind("Sync", "SyncConfig", true, "Copy Workshop BepInEx/config content.");
			_copyConfigToRoot = Config.Bind("Sync", "CopyConfigToRoot", true, "Copy config files into BepInEx/config using their original relative paths. Disable to place them under BepInEx/config/Workshop/<workshop id>.");
			_overwriteUnmanagedFiles = Config.Bind("Safety", "OverwriteUnmanagedFiles", false, "Allow the bridge to overwrite files it did not create previously.");
			_removeOrphanedFiles = Config.Bind("Safety", "RemoveOrphanedManagedFiles", true, "Remove files previously copied by the bridge when their Workshop source disappears.");

			try
			{
				SyncResult result = SyncWorkshopPayloads();
				Logger.LogInfo("Workshop bridge scan complete. Mode=" + result.Mode + ", roots=" + result.RootsScanned + ", items=" + result.ItemsScanned + ", copied=" + result.Copied + ", skipped=" + result.Skipped + ", removed=" + result.Removed + ".");
				if (result.Copied > 0 || result.Removed > 0)
				{
					Logger.LogWarning("BepInEx plugin or patcher changes usually require restarting Ostranauts before they take effect.");
				}
			}
			catch (Exception ex)
			{
				Logger.LogError("Workshop bridge failed: " + ex);
			}
		}

		private SyncResult SyncWorkshopPayloads()
		{
			string bepinexRoot = NormalizeFullPath(Paths.BepInExRootPath);
			string manifestPath = Path.Combine(Paths.ConfigPath, ManifestFileName);
			List<ManifestEntry> previousEntries = LoadManifest(manifestPath);
			Dictionary<string, ManifestEntry> previousByDest = previousEntries
				.GroupBy(e => e.DestinationRelative, StringComparer.OrdinalIgnoreCase)
				.ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
			List<CopySpec> specs = new List<CopySpec>();
			SyncResult result = new SyncResult();

			List<string> itemDirs;
			if (TryGetLoadingOrderWorkshopItemDirs(out itemDirs))
			{
				result.Mode = "loading_order";
			}
			else if (_fallbackToWorkshopFolderScan.Value)
			{
				result.Mode = "folder_scan";
				itemDirs = GetAllWorkshopItemDirs(result).ToList();
			}
			else
			{
				result.Mode = "loading_order_missing";
				Logger.LogWarning("No loading_order.json was found. Skipping Workshop BepInEx sync and cleanup.");
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
				AddPayloadSpecs(specs, workshopId, sourceBepInEx, "plugins", Path.Combine(Paths.PluginPath, ManagedFolderName, workshopId), bepinexRoot);
				if (_syncPatchers.Value)
				{
					AddPayloadSpecs(specs, workshopId, sourceBepInEx, "patchers", Path.Combine(Paths.PatcherPluginPath, ManagedFolderName, workshopId), bepinexRoot);
				}
				if (_syncConfig.Value)
				{
					string configDest = _copyConfigToRoot.Value ? Paths.ConfigPath : Path.Combine(Paths.ConfigPath, ManagedFolderName, workshopId);
					AddPayloadSpecs(specs, workshopId, sourceBepInEx, "config", configDest, bepinexRoot);
				}
			}

			HashSet<string> currentDestinations = new HashSet<string>(specs.Select(s => s.DestinationRelative), StringComparer.OrdinalIgnoreCase);
			if (_removeOrphanedFiles.Value)
			{
				foreach (ManifestEntry entry in previousEntries)
				{
					if (!currentDestinations.Contains(entry.DestinationRelative) && TryRemoveManagedFile(bepinexRoot, entry))
					{
						result.Removed++;
					}
				}
			}

			List<ManifestEntry> nextManifest = new List<ManifestEntry>();
			HashSet<string> plannedDestinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (CopySpec spec in specs.OrderBy(s => s.WorkshopId, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.DestinationRelative, StringComparer.OrdinalIgnoreCase))
			{
				if (!plannedDestinations.Add(spec.DestinationRelative))
				{
					Logger.LogWarning("Skipped duplicate destination from Workshop item " + spec.WorkshopId + ": " + spec.DestinationRelative);
					result.Skipped++;
					continue;
				}

				ManifestEntry previous;
				bool wasManaged = previousByDest.TryGetValue(spec.DestinationRelative, out previous);
				if (!CanWriteDestination(spec, previous, wasManaged))
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
			RemoveEmptyManagedDirectories();
			return result;
		}

		private bool TryGetLoadingOrderWorkshopItemDirs(out List<string> itemDirs)
		{
			itemDirs = new List<string>();
			string loadingOrderPath = GetLoadingOrderPath();
			if (string.IsNullOrEmpty(loadingOrderPath))
			{
				return false;
			}

			Logger.LogInfo("Using loading order: " + loadingOrderPath);
			HashSet<string> deduped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			HashSet<string> workshopRoots = new HashSet<string>(GetWorkshopRoots().Select(NormalizeFullPath), StringComparer.OrdinalIgnoreCase);
			foreach (string entry in ReadLoadingOrderEntries(loadingOrderPath))
			{
				string modPath;
				if (!TryGetEnabledModPath(entry, out modPath))
				{
					continue;
				}
				if (string.IsNullOrWhiteSpace(modPath) || string.Equals(modPath, "core", StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}

				if (!Path.IsPathRooted(modPath))
				{
					continue;
				}

				string normalized = NormalizeFullPath(modPath);
				if (!IsWorkshopItemPath(normalized, workshopRoots))
				{
					continue;
				}

				deduped.Add(normalized);
			}

			itemDirs.AddRange(deduped.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
			return true;
		}

		private IEnumerable<string> GetAllWorkshopItemDirs(SyncResult result)
		{
			foreach (string workshopRoot in GetWorkshopRoots())
			{
				if (!Directory.Exists(workshopRoot))
				{
					Logger.LogDebug("Workshop root not found: " + workshopRoot);
					continue;
				}

				result.RootsScanned++;
				foreach (string itemDir in Directory.GetDirectories(workshopRoot))
				{
					yield return NormalizeFullPath(itemDir);
				}
			}
		}

		private string GetLoadingOrderPath()
		{
			foreach (string candidate in GetLoadingOrderPathCandidates())
			{
				if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
				{
					return NormalizeFullPath(candidate);
				}
			}

			return null;
		}

		private IEnumerable<string> GetLoadingOrderPathCandidates()
		{
			if (!string.IsNullOrWhiteSpace(_loadingOrderPathOverride.Value))
			{
				yield return _loadingOrderPathOverride.Value;
			}

			string settingsPath = Path.Combine(Application.persistentDataPath, "settings.json");
			string configuredModPath = ReadSettingsString(settingsPath, "strPathMods");
			if (!string.IsNullOrWhiteSpace(configuredModPath))
			{
				yield return configuredModPath;
			}

			yield return Path.Combine(Application.dataPath, "Mods", "loading_order.json");
			yield return Path.Combine(Paths.GameRootPath, "Ostranauts_Data", "Mods", "loading_order.json");
		}

		private IEnumerable<string> ReadLoadingOrderEntries(string loadingOrderPath)
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

		private static string ReadSettingsString(string settingsPath, string propertyName)
		{
			if (!File.Exists(settingsPath))
			{
				return null;
			}

			string text = File.ReadAllText(settingsPath);
			Match match = Regex.Match(text, "\"" + Regex.Escape(propertyName) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");
			return match.Success ? UnescapeJsonString(match.Groups["value"].Value) : null;
		}

		private static bool TryGetEnabledModPath(string loadOrderEntry, out string modPath)
		{
			modPath = null;
			string[] parts = loadOrderEntry.Split('|');
			if (parts.Length > 2)
			{
				return false;
			}

			if (parts.Length == 2 && string.Equals(parts[1], "disabled", StringComparison.OrdinalIgnoreCase))
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

		private void AddPayloadSpecs(List<CopySpec> specs, string workshopId, string sourceBepInEx, string payloadName, string destinationRoot, string bepinexRoot)
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
				if (!IsInsideDirectory(destinationFull, bepinexRoot))
				{
					Logger.LogWarning("Skipped unsafe destination path: " + destinationFull);
					continue;
				}

				specs.Add(new CopySpec
				{
					WorkshopId = workshopId,
					SourceFull = NormalizeFullPath(sourceFile),
					DestinationFull = destinationFull,
					SourceRelative = NormalizeSlashes(Path.Combine(workshopId, "BepInEx", payloadName, relativeToPayload)),
					DestinationRelative = MakeRelativePath(bepinexRoot, destinationFull)
				});
			}
		}

		private bool ShouldSkipSourceFile(string sourceFile)
		{
			string fileName = Path.GetFileName(sourceFile);
			string assemblyName = Path.GetFileName(Assembly.GetExecutingAssembly().Location);
			return string.Equals(fileName, assemblyName, StringComparison.OrdinalIgnoreCase);
		}

		private bool CanWriteDestination(CopySpec spec, ManifestEntry previous, bool wasManaged)
		{
			if (!File.Exists(spec.DestinationFull))
			{
				return true;
			}

			if (wasManaged)
			{
				return true;
			}

			if (FilesAlreadyMatch(spec.SourceFull, spec.DestinationFull))
			{
				return true;
			}

			if (_overwriteUnmanagedFiles.Value)
			{
				Logger.LogWarning("Overwriting unmanaged file: " + spec.DestinationRelative);
				return true;
			}

			Logger.LogWarning("Skipped unmanaged existing file: " + spec.DestinationRelative);
			return false;
		}

		private bool TryRemoveManagedFile(string bepinexRoot, ManifestEntry entry)
		{
			string destination = NormalizeFullPath(Path.Combine(bepinexRoot, entry.DestinationRelative));
			if (!IsInsideDirectory(destination, bepinexRoot) || !File.Exists(destination))
			{
				return false;
			}

			FileInfo fileInfo = new FileInfo(destination);
			if (fileInfo.Length != entry.Length || fileInfo.LastWriteTimeUtc.Ticks != entry.LastWriteTicksUtc)
			{
				Logger.LogWarning("Kept modified managed file: " + entry.DestinationRelative);
				return false;
			}

			File.Delete(destination);
			return true;
		}

		private IEnumerable<string> GetWorkshopRoots()
		{
			HashSet<string> roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrWhiteSpace(_workshopRootOverride.Value))
			{
				roots.Add(NormalizeFullPath(_workshopRootOverride.Value));
			}

			string gameRoot = NormalizeFullPath(Paths.GameRootPath);
			DirectoryInfo gameDir = new DirectoryInfo(gameRoot);
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

		private IEnumerable<string> ReadSteamLibraryFolders(string steamAppsPath)
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

		private List<ManifestEntry> LoadManifest(string manifestPath)
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

		private void SaveManifest(string manifestPath, IEnumerable<ManifestEntry> entries)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(manifestPath));
			File.WriteAllLines(manifestPath, entries.Select(e => e.ToLine()).ToArray());
		}

		private void RemoveEmptyManagedDirectories()
		{
			RemoveEmptyDirectories(Path.Combine(Paths.PluginPath, ManagedFolderName));
			RemoveEmptyDirectories(Path.Combine(Paths.PatcherPluginPath, ManagedFolderName));
			if (!_copyConfigToRoot.Value)
			{
				RemoveEmptyDirectories(Path.Combine(Paths.ConfigPath, ManagedFolderName));
			}
		}

		private void RemoveEmptyDirectories(string root)
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
			public int RootsScanned;
			public int ItemsScanned;
			public int Copied;
			public int Skipped;
			public int Removed;
		}
	}
}
