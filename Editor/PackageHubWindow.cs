using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;
using UnityEngine.Networking;

namespace AreezKhan79.PackageHub.Editor
{
    internal class PackageHubWindow : EditorWindow
    {
        private static readonly Regex ManifestEntryRegex =
            new Regex("\"(?<name>[^\"]+)\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled);

        private static readonly Regex SemverRegex =
            new Regex(@"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)", RegexOptions.Compiled);

        private class PackageUiState
        {
            public bool selected;
            public bool wasInstalled;
            public string[] versions;
            public int selectedVersionIndex = -1;
            public bool isFetchingVersions;
            public string fetchError;
            public string installedVersion;
            public string pendingPresetVersion;
        }

        private class ApplyDiff
        {
            public readonly List<string> ToAdd = new List<string>();
            public readonly List<string> ToRemove = new List<string>();
            public readonly List<string> SummaryLines = new List<string>();
        }

        private Registry _registry;
        private bool _isLoadingRegistry;
        private string _loadError;
        private readonly Dictionary<string, PackageUiState> _uiState = new Dictionary<string, PackageUiState>();
        private Vector2 _scroll;

        private bool _isApplying;
        private string _applyStatus;
        private bool _applyIsError;
        private ApplyDiff _pendingDiff;
        private AddAndRemoveRequest _pendingRequest;

        private bool _showSettings;
        private string _newRegistryUrl = "";
        private string _searchQuery = "";

        private const string UncategorizedLabel = "Other";
        private readonly Dictionary<string, bool> _categoryFoldout = new Dictionary<string, bool>();

        [MenuItem("Window/Package Hub")]
        private static void Open()
        {
            var window = GetWindow<PackageHubWindow>();
            window.titleContent = new GUIContent("Package Hub");
            window.minSize = new Vector2(440, 320);
        }

        private void OnEnable()
        {
            _showSettings = PackageHubSettings.instance.RegistryUrls.Count == 0;
            FetchAllRegistries();
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollApplyRequest;
        }

        private void FetchAllRegistries()
        {
            var urls = PackageHubSettings.instance.RegistryUrls;
            _registry = new Registry { packages = Array.Empty<PackageEntry>() };
            _pendingDiff = null;

            if (urls.Count == 0)
            {
                _isLoadingRegistry = false;
                _loadError = null;
                BuildUiState();
                Repaint();
                return;
            }

            _isLoadingRegistry = true;
            _loadError = null;

            var pending = urls.Count;
            var collected = new List<PackageEntry>();
            var errors = new List<string>();

            foreach (var url in urls)
            {
                var request = UnityWebRequest.Get(url);
                var op = request.SendWebRequest();
                op.completed += _ =>
                {
                    if (request.result == UnityWebRequest.Result.Success)
                    {
                        try
                        {
                            var parsed = JsonUtility.FromJson<Registry>(request.downloadHandler.text);
                            if (parsed?.packages != null) collected.AddRange(parsed.packages);
                        }
                        catch (Exception e)
                        {
                            errors.Add($"{url}: failed to parse - {e.Message}");
                        }
                    }
                    else
                    {
                        errors.Add($"{url}: {request.error}");
                    }

                    pending--;
                    if (pending > 0) return;

                    _isLoadingRegistry = false;
                    _registry = new Registry { packages = collected.ToArray() };
                    _loadError = errors.Count > 0 ? string.Join("\n", errors) : null;
                    BuildUiState();
                    Repaint();
                };
            }
        }

        private void BuildUiState()
        {
            _uiState.Clear();
            if (_registry?.packages == null) return;

            var installed = ReadInstalledManifestEntries();
            foreach (var pkg in _registry.packages)
            {
                var state = new PackageUiState();
                if (installed.TryGetValue(pkg.name, out var tag))
                {
                    state.selected = true;
                    state.wasInstalled = true;
                    state.installedVersion = tag;
                }

                _uiState[pkg.name] = state;

                if (state.selected)
                {
                    FetchVersions(pkg, state);
                }
            }
        }

        private static Dictionary<string, string> ReadInstalledManifestEntries()
        {
            var result = new Dictionary<string, string>();
            var manifestPath = Path.Combine(Application.dataPath, "..", "Packages", "manifest.json");
            if (!File.Exists(manifestPath)) return result;

            var json = File.ReadAllText(manifestPath);
            foreach (Match match in ManifestEntryRegex.Matches(json))
            {
                var value = match.Groups["value"].Value;
                if (!value.Contains(".git")) continue;

                var name = match.Groups["name"].Value;
                var hashIndex = value.IndexOf('#');
                var tag = hashIndex >= 0 ? value.Substring(hashIndex + 1) : null;
                result[name] = tag;
            }

            return result;
        }

        private void FetchVersions(PackageEntry pkg, PackageUiState state)
        {
            state.isFetchingVersions = true;
            state.fetchError = null;

            var (owner, repo) = ParseOwnerRepo(pkg.repoUrl);
            var url = $"https://api.github.com/repos/{owner}/{repo}/tags";
            var request = UnityWebRequest.Get(url);
            request.SetRequestHeader("User-Agent", "UnityPackageHub");
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                state.isFetchingVersions = false;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    state.fetchError = $"Failed to fetch tags ({request.responseCode}): {request.error}";
                    Repaint();
                    return;
                }

                try
                {
                    var wrapped = "{\"items\":" + request.downloadHandler.text + "}";
                    var parsed = JsonUtility.FromJson<GitTagListWrapper>(wrapped);
                    var names = parsed.items?.Select(t => t.name) ?? Enumerable.Empty<string>();
                    state.versions = names.OrderBy(v => v, Comparer<string>.Create(CompareVersionsDescending)).ToArray();

                    if (state.versions.Length > 0)
                    {
                        var idx = !string.IsNullOrEmpty(state.installedVersion)
                            ? Array.IndexOf(state.versions, state.installedVersion)
                            : -1;
                        state.selectedVersionIndex = idx >= 0 ? idx : 0;
                    }

                    if (!string.IsNullOrEmpty(state.pendingPresetVersion))
                    {
                        var presetIdx = Array.IndexOf(state.versions, state.pendingPresetVersion);
                        if (presetIdx >= 0) state.selectedVersionIndex = presetIdx;
                        state.pendingPresetVersion = null;
                    }
                }
                catch (Exception e)
                {
                    state.fetchError = $"Failed to parse tags: {e.Message}";
                }

                Repaint();
            };
        }

        private static (int major, int minor, int patch)? ParseSemver(string tag)
        {
            var m = SemverRegex.Match(tag);
            if (!m.Success) return null;
            return (
                int.Parse(m.Groups["major"].Value),
                int.Parse(m.Groups["minor"].Value),
                int.Parse(m.Groups["patch"].Value));
        }

        private static int CompareVersionsDescending(string x, string y)
        {
            var px = ParseSemver(x);
            var py = ParseSemver(y);

            if (px.HasValue && py.HasValue) return px.Value.CompareTo(py.Value) * -1;
            if (px.HasValue) return -1;
            if (py.HasValue) return 1;
            return string.CompareOrdinal(y, x);
        }

        private static (string owner, string repo) ParseOwnerRepo(string repoUrl)
        {
            var trimmed = repoUrl.TrimEnd('/');
            if (trimmed.EndsWith(".git")) trimmed = trimmed.Substring(0, trimmed.Length - 4);
            var parts = trimmed.Split('/');
            return (parts[parts.Length - 2], parts[parts.Length - 1]);
        }

        private IEnumerable<PackageEntry> GetFilteredPackages()
        {
            if (_registry?.packages == null) yield break;

            var query = _searchQuery?.Trim();
            foreach (var pkg in _registry.packages)
            {
                if (string.IsNullOrEmpty(query) || MatchesQuery(pkg, query))
                {
                    yield return pkg;
                }
            }
        }

        private static bool MatchesQuery(PackageEntry pkg, string query)
        {
            return Contains(pkg.displayName, query) || Contains(pkg.name, query) || Contains(pkg.description, query);
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Package Hub", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Refresh", "Re-fetch registry.json from all configured registries"), GUILayout.Width(70)))
                {
                    FetchAllRegistries();
                }
            }

            EditorGUILayout.Space();

            DrawSettings();

            if (_isLoadingRegistry)
            {
                EditorGUILayout.HelpBox("Loading registries...", MessageType.Info);
                return;
            }

            if (_loadError != null)
            {
                EditorGUILayout.HelpBox(_loadError, MessageType.Error);
            }

            if (PackageHubSettings.instance.RegistryUrls.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "No registries configured yet. Add a registry.json URL above to start browsing packages.",
                    MessageType.Info);
                return;
            }

            if (_registry?.packages == null || _registry.packages.Length == 0)
            {
                EditorGUILayout.HelpBox("Registries are configured but returned no packages.", MessageType.Warning);
                return;
            }

            _searchQuery = EditorGUILayout.TextField(
                new GUIContent("Search", "Filter the list below by name or description"), _searchQuery);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(new GUIContent("Select All", "Check every package currently shown below")))
                {
                    SetSelectedForVisible(true);
                }

                if (GUILayout.Button(new GUIContent("Select None", "Uncheck every package currently shown below")))
                {
                    SetSelectedForVisible(false);
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(new GUIContent("Save Preset...",
                        "Save the current checked packages + versions as a reusable .json file")))
                {
                    SavePresetToFile();
                }

                if (GUILayout.Button(new GUIContent("Load Preset...",
                        "Load a .json preset, replacing current selections to match it exactly")))
                {
                    LoadPresetFromFile();
                }
            }

            EditorGUILayout.Space();

            var visible = GetFilteredPackages().ToList();
            var requiredByMap = BuildRequiredByMap();

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            if (visible.Count == 0)
            {
                EditorGUILayout.HelpBox("No packages match your search.", MessageType.Info);
            }
            else
            {
                var groups = visible
                    .GroupBy(GetCategory)
                    .OrderBy(g => g.Key == UncategorizedLabel ? 1 : 0)
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

                foreach (var group in groups)
                {
                    DrawCategoryGroup(group.Key, group.ToList(), requiredByMap);
                }
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();

            if (_pendingDiff != null)
            {
                DrawPendingDiff();
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.FlexibleSpace();
                    GUI.enabled = !_isApplying;
                    if (GUILayout.Button(
                            new GUIContent("Apply to Project", "Review what will change before installing/removing anything"),
                            GUILayout.Width(150), GUILayout.Height(28)))
                    {
                        BeginApply();
                    }

                    GUI.enabled = true;
                }
            }

            if (_applyStatus != null)
            {
                EditorGUILayout.HelpBox(_applyStatus, _applyIsError ? MessageType.Error : MessageType.Info);
            }
        }

        private void SavePresetToFile()
        {
            var entries = new List<PresetEntry>();
            foreach (var pkg in _registry.packages)
            {
                var state = _uiState[pkg.name];
                if (!state.selected) continue;
                if (state.versions == null || state.selectedVersionIndex < 0 ||
                    state.selectedVersionIndex >= state.versions.Length) continue;

                entries.Add(new PresetEntry
                {
                    packageName = pkg.name,
                    version = state.versions[state.selectedVersionIndex]
                });
            }

            if (entries.Count == 0)
            {
                _applyStatus = "Nothing selected (with a resolved version) to save as a preset.";
                _applyIsError = true;
                return;
            }

            var path = EditorUtility.SaveFilePanel("Save Package Preset", "", "preset", "json");
            if (string.IsNullOrEmpty(path)) return;

            var preset = new Preset
            {
                presetName = Path.GetFileNameWithoutExtension(path),
                entries = entries.ToArray()
            };

            File.WriteAllText(path, JsonUtility.ToJson(preset, true));
            _applyStatus = $"Saved preset '{preset.presetName}' with {entries.Count} package(s) to {path}.";
            _applyIsError = false;
        }

        private void LoadPresetFromFile()
        {
            var path = EditorUtility.OpenFilePanel("Load Package Preset", "", "json");
            if (string.IsNullOrEmpty(path)) return;

            Preset preset;
            try
            {
                preset = JsonUtility.FromJson<Preset>(File.ReadAllText(path));
            }
            catch (Exception e)
            {
                _applyStatus = $"Failed to load preset: {e.Message}";
                _applyIsError = true;
                return;
            }

            if (preset?.entries == null || preset.entries.Length == 0)
            {
                _applyStatus = "Preset file is empty or invalid.";
                _applyIsError = true;
                return;
            }

            var wanted = preset.entries.ToDictionary(e => e.packageName, e => e.version);
            var missing = new List<string>(wanted.Keys);

            foreach (var pkg in _registry.packages)
            {
                var state = _uiState[pkg.name];
                if (wanted.TryGetValue(pkg.name, out var version))
                {
                    missing.Remove(pkg.name);
                    state.selected = true;

                    if (state.versions != null)
                    {
                        var idx = Array.IndexOf(state.versions, version);
                        state.selectedVersionIndex = idx >= 0 ? idx : 0;
                    }
                    else
                    {
                        state.pendingPresetVersion = version;
                        if (!state.isFetchingVersions) FetchVersions(pkg, state);
                    }
                }
                else
                {
                    state.selected = false;
                }
            }

            // Pull in any dependencies the preset didn't explicitly list.
            foreach (var pkg in _registry.packages)
            {
                if (_uiState[pkg.name].selected)
                {
                    SelectPackageWithDependencies(pkg);
                }
            }

            _pendingDiff = null;
            _applyStatus = missing.Count > 0
                ? $"Preset '{preset.presetName}' applied. Not found in configured registries: {string.Join(", ", missing)}"
                : $"Preset '{preset.presetName}' applied. Review versions, then Apply to Project.";
            _applyIsError = missing.Count > 0;
        }

        private static string GetCategory(PackageEntry pkg) =>
            string.IsNullOrWhiteSpace(pkg.category) ? UncategorizedLabel : pkg.category;

        private PackageEntry FindPackage(string packageName)
        {
            if (_registry?.packages == null) return null;
            foreach (var pkg in _registry.packages)
            {
                if (pkg.name == packageName) return pkg;
            }

            return null;
        }

        private Dictionary<string, List<string>> BuildRequiredByMap()
        {
            var map = new Dictionary<string, List<string>>();
            if (_registry?.packages == null) return map;

            foreach (var pkg in _registry.packages)
            {
                if (pkg.dependencies == null) continue;
                if (!_uiState.TryGetValue(pkg.name, out var state) || !state.selected) continue;

                foreach (var depName in pkg.dependencies)
                {
                    if (!map.TryGetValue(depName, out var requirers))
                    {
                        requirers = new List<string>();
                        map[depName] = requirers;
                    }

                    requirers.Add(pkg.displayName);
                }
            }

            return map;
        }

        private void SelectPackageWithDependencies(PackageEntry pkg, HashSet<string> visited = null)
        {
            if (visited == null) visited = new HashSet<string>();
            if (!visited.Add(pkg.name)) return;

            var state = _uiState[pkg.name];
            state.selected = true;
            if (state.versions == null && !state.isFetchingVersions)
            {
                FetchVersions(pkg, state);
            }

            if (pkg.dependencies == null) return;

            foreach (var depName in pkg.dependencies)
            {
                var dep = FindPackage(depName);
                if (dep != null && _uiState.ContainsKey(dep.name))
                {
                    SelectPackageWithDependencies(dep, visited);
                }
            }
        }

        private void DrawCategoryGroup(string category, List<PackageEntry> packages, Dictionary<string, List<string>> requiredByMap)
        {
            if (!_categoryFoldout.TryGetValue(category, out var expanded))
            {
                expanded = true;
            }

            expanded = EditorGUILayout.Foldout(expanded, $"{category} ({packages.Count})", true, EditorStyles.foldoutHeader);
            _categoryFoldout[category] = expanded;

            if (!expanded)
            {
                EditorGUILayout.Space(2);
                return;
            }

            foreach (var pkg in packages)
            {
                DrawPackageRow(pkg, _uiState[pkg.name], requiredByMap);
            }

            EditorGUILayout.Space(4);
        }

        private void SetSelectedForVisible(bool selected)
        {
            foreach (var pkg in GetFilteredPackages())
            {
                if (selected)
                {
                    SelectPackageWithDependencies(pkg);
                }
                else
                {
                    _uiState[pkg.name].selected = false;
                }
            }
        }

        private void DrawSettings()
        {
            _showSettings = EditorGUILayout.Foldout(_showSettings, "Settings", true);
            if (!_showSettings)
            {
                EditorGUILayout.Space();
                return;
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(
                    "Registries are raw registry.json URLs. Add one to browse its packages here.",
                    EditorStyles.wordWrappedMiniLabel);

                var settings = PackageHubSettings.instance;
                string urlToRemove = null;

                foreach (var url in settings.RegistryUrls)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(url);
                        if (GUILayout.Button(new GUIContent("Remove", "Stop reading packages from this registry"), GUILayout.Width(60)))
                        {
                            urlToRemove = url;
                        }
                    }
                }

                if (urlToRemove != null)
                {
                    settings.RemoveRegistry(urlToRemove);
                    FetchAllRegistries();
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    _newRegistryUrl = EditorGUILayout.TextField(_newRegistryUrl);
                    GUI.enabled = !string.IsNullOrWhiteSpace(_newRegistryUrl);
                    if (GUILayout.Button(
                            new GUIContent("Add Registry", "Fetch and merge packages from this registry.json URL"),
                            GUILayout.Width(100)))
                    {
                        settings.AddRegistry(_newRegistryUrl.Trim());
                        _newRegistryUrl = "";
                        GUI.FocusControl(null);
                        FetchAllRegistries();
                    }

                    GUI.enabled = true;
                }
            }

            EditorGUILayout.Space();
        }

        private void DrawPackageRow(PackageEntry pkg, PackageUiState state, Dictionary<string, List<string>> requiredByMap)
        {
            requiredByMap.TryGetValue(pkg.name, out var requiredBy);
            var isLocked = requiredBy != null && requiredBy.Count > 0 && state.selected;

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var wasSelected = state.selected;

                    GUI.enabled = !isLocked;
                    var newSelected = EditorGUILayout.ToggleLeft(
                        new GUIContent(pkg.displayName, pkg.repoUrl), state.selected, EditorStyles.boldLabel,
                        GUILayout.Width(220));
                    GUI.enabled = true;

                    if (newSelected != wasSelected)
                    {
                        if (newSelected)
                        {
                            SelectPackageWithDependencies(pkg);
                        }
                        else
                        {
                            state.selected = false;
                        }
                    }

                    GUILayout.FlexibleSpace();

                    if (!string.IsNullOrEmpty(state.installedVersion))
                    {
                        EditorGUILayout.LabelField(
                            new GUIContent($"installed: {state.installedVersion}", "Version currently in this project's manifest.json"),
                            GUILayout.Width(160));
                    }
                }

                EditorGUILayout.LabelField(pkg.description, EditorStyles.wordWrappedMiniLabel);

                if (isLocked)
                {
                    EditorGUILayout.LabelField(
                        $"Required by: {string.Join(", ", requiredBy)}", EditorStyles.miniLabel);
                }

                if (state.selected)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(
                            new GUIContent("Version", "Which git tag to install. Sorted newest first when tags look like semver."),
                            GUILayout.Width(60));

                        if (state.isFetchingVersions)
                        {
                            EditorGUILayout.LabelField("Fetching versions...");
                        }
                        else if (state.fetchError != null)
                        {
                            EditorGUILayout.LabelField(state.fetchError);
                            if (GUILayout.Button("Retry", GUILayout.Width(60)))
                            {
                                FetchVersions(pkg, state);
                            }
                        }
                        else if (state.versions != null && state.versions.Length > 0)
                        {
                            state.selectedVersionIndex = EditorGUILayout.Popup(
                                Mathf.Max(state.selectedVersionIndex, 0), state.versions);
                        }
                        else if (state.versions != null)
                        {
                            EditorGUILayout.LabelField("No tags found for this repo");
                        }
                        else
                        {
                            EditorGUILayout.LabelField("Not fetched yet");
                            if (GUILayout.Button("Fetch", GUILayout.Width(60)))
                            {
                                FetchVersions(pkg, state);
                            }
                        }
                    }
                }
            }

            EditorGUILayout.Space(4);
        }

        private void BeginApply()
        {
            var diff = new ApplyDiff();

            foreach (var pkg in _registry.packages)
            {
                var state = _uiState[pkg.name];

                if (state.selected)
                {
                    if (state.versions == null || state.versions.Length == 0 || state.selectedVersionIndex < 0)
                    {
                        _applyStatus = $"Cannot add {pkg.displayName}: no version selected or available yet.";
                        _applyIsError = true;
                        _pendingDiff = null;
                        return;
                    }

                    var tag = state.versions[state.selectedVersionIndex];
                    if (state.installedVersion == tag) continue;

                    diff.ToAdd.Add($"{pkg.repoUrl}#{tag}");
                    diff.SummaryLines.Add(state.wasInstalled
                        ? $"Upgrade {pkg.displayName}: {state.installedVersion} -> {tag}"
                        : $"Add {pkg.displayName} @ {tag}");
                }
                else if (state.wasInstalled)
                {
                    diff.ToRemove.Add(pkg.name);
                    diff.SummaryLines.Add($"Remove {pkg.displayName}");
                }
            }

            if (diff.ToAdd.Count == 0 && diff.ToRemove.Count == 0)
            {
                _applyStatus = "Nothing to change - selections already match the project.";
                _applyIsError = false;
                _pendingDiff = null;
                return;
            }

            _applyStatus = null;
            _pendingDiff = diff;
        }

        private void DrawPendingDiff()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("Pending changes", EditorStyles.boldLabel);
                foreach (var line in _pendingDiff.SummaryLines)
                {
                    EditorGUILayout.LabelField("• " + line, EditorStyles.wordWrappedMiniLabel);
                }

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUI.enabled = !_isApplying;
                    if (GUILayout.Button(new GUIContent("Confirm", "Actually install/remove the packages listed above")))
                    {
                        ExecuteDiff(_pendingDiff);
                    }

                    if (GUILayout.Button(new GUIContent("Cancel", "Discard this preview without changing anything")))
                    {
                        _pendingDiff = null;
                    }

                    GUI.enabled = true;
                }
            }
        }

        private void ExecuteDiff(ApplyDiff diff)
        {
            _isApplying = true;
            _applyStatus = "Applying changes...";
            _applyIsError = false;
            _pendingDiff = null;
            _pendingRequest = Client.AddAndRemove(diff.ToAdd.ToArray(), diff.ToRemove.ToArray());
            EditorApplication.update += PollApplyRequest;
        }

        private void PollApplyRequest()
        {
            if (_pendingRequest == null || !_pendingRequest.IsCompleted) return;

            EditorApplication.update -= PollApplyRequest;
            _isApplying = false;

            if (_pendingRequest.Status == StatusCode.Success)
            {
                _applyStatus = "Applied. Unity is resolving packages in the background - watch the bottom-right corner.";
                _applyIsError = false;
                BuildUiState();
            }
            else
            {
                _applyStatus = $"Failed: {_pendingRequest.Error?.message}";
                _applyIsError = true;
            }

            Repaint();
        }
    }
}
