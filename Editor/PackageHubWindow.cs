using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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

        private const string TokenPrefKey = "AreezKhan79.PackageHub.GitHubToken";

        private static string GitHubToken
        {
            get => EditorPrefs.GetString(TokenPrefKey, "");
            set => EditorPrefs.SetString(TokenPrefKey, value);
        }

        private static void ApplyAuthHeader(UnityWebRequest request)
        {
            var token = GitHubToken;
            if (!string.IsNullOrEmpty(token))
            {
                request.SetRequestHeader("Authorization", "Bearer " + token);
            }
        }

        private bool _showCreateRegistry;
        private string _newRegistryRepoName = "upm-registry";
        private string _newRegistryDescription = "My personal Unity package registry.";
        private bool _newRegistryPrivate;
        private bool _isCreatingRegistry;
        private string _createRegistryStatus;
        private bool _createRegistryIsError;

        private static readonly Regex PackageNameRegex =
            new Regex(@"^[a-z0-9]+(\.[a-z0-9\-]+){2,}$", RegexOptions.Compiled);

        private bool _showCreatePackage;
        private string _newPackageName = "com.areezkhan79.";
        private string _newPackageDisplayName = "";
        private string _newPackageDescription = "";
        private string _newPackageCategory = "";
        private string _newPackageFolder = "";
        private string _newPackageRepoName = "";
        private bool _newPackagePrivate;
        private int _newPackageRegistryIndex;
        private bool _isCreatingPackage;
        private string _createPackageStatus;
        private bool _createPackageIsError;

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
                ApplyAuthHeader(request);
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
            ApplyAuthHeader(request);
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
            DrawCreateRegistrySection();
            DrawCreatePackageSection();

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

            DrawStatusMessage(ref _applyStatus, _applyIsError, dismissable: false);
        }

        private static void DrawStatusMessage(ref string status, bool isError, bool dismissable = true)
        {
            if (status == null) return;

            var current = status;
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.HelpBox(current, isError ? MessageType.Error : MessageType.Info);

                using (new EditorGUILayout.VerticalScope(GUILayout.Width(20)))
                {
                    if (GUILayout.Button(new GUIContent("C", "Copy this message to the clipboard"), GUILayout.Width(20)))
                    {
                        EditorGUIUtility.systemCopyBuffer = current;
                    }

                    if (dismissable && GUILayout.Button(new GUIContent("x", "Dismiss"), GUILayout.Width(20)))
                    {
                        status = null;
                    }
                }
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
                    _createRegistryStatus = null;
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

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("GitHub Token (optional)", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "Raises the 60/hour unauthenticated API rate limit and enables access to private repos. " +
                    "Stored locally via EditorPrefs on this machine only - never committed to the project. " +
                    "No scopes needed for public repos; 'repo' scope for private ones.",
                    EditorStyles.wordWrappedMiniLabel);

                var currentToken = GitHubToken;
                var newToken = EditorGUILayout.PasswordField("Token", currentToken);
                if (newToken != currentToken)
                {
                    GitHubToken = newToken;
                }
            }

            EditorGUILayout.Space();
        }

        private void DrawCreateRegistrySection()
        {
            _showCreateRegistry = EditorGUILayout.Foldout(_showCreateRegistry, "Create New Registry", true);

            if (_showCreateRegistry)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        "Creates a new GitHub repository with a starter registry.json and adds it to your " +
                        "registries above - no terminal or git commands needed. Requires a GitHub token with " +
                        "repo-creation rights, set in Settings above.",
                        EditorStyles.wordWrappedMiniLabel);

                    _newRegistryRepoName = EditorGUILayout.TextField(
                        new GUIContent("Repo name", "The new GitHub repository's name"), _newRegistryRepoName);
                    _newRegistryDescription = EditorGUILayout.TextField(
                        new GUIContent("Description", "Shown on the GitHub repo page"), _newRegistryDescription);
                    _newRegistryPrivate = EditorGUILayout.Toggle(
                        new GUIContent("Private", "Private repos also require a token with 'repo' scope for browsing"),
                        _newRegistryPrivate);

                    if (string.IsNullOrEmpty(GitHubToken))
                    {
                        EditorGUILayout.HelpBox("Add a GitHub token above first.", MessageType.Warning);
                    }

                    GUI.enabled = !_isCreatingRegistry && !string.IsNullOrEmpty(GitHubToken) &&
                                  !string.IsNullOrWhiteSpace(_newRegistryRepoName);
                    if (GUILayout.Button(_isCreatingRegistry ? "Creating..." : "Create Registry"))
                    {
                        CreateNewRegistry();
                    }

                    GUI.enabled = true;
                }
            }

            // Rendered outside the foldout body so it's still visible even after
            // a successful creation collapses the section.
            DrawStatusMessage(ref _createRegistryStatus, _createRegistryIsError);

            EditorGUILayout.Space();
        }

        private static UnityWebRequest CreateJsonRequest(string url, string method, string jsonBody)
        {
            var request = new UnityWebRequest(url, method)
            {
                downloadHandler = new DownloadHandlerBuffer()
            };

            if (!string.IsNullOrEmpty(jsonBody))
            {
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody));
            }

            request.SetRequestHeader("Content-Type", "application/json");
            request.SetRequestHeader("User-Agent", "UnityPackageHub");
            ApplyAuthHeader(request);
            return request;
        }

        private void CreateNewRegistry()
        {
            _isCreatingRegistry = true;
            _createRegistryStatus = "Creating repository...";
            _createRegistryIsError = false;

            var body = JsonUtility.ToJson(new CreateRepoRequest
            {
                name = _newRegistryRepoName.Trim(),
                description = _newRegistryDescription,
                @private = _newRegistryPrivate
            });

            var request = CreateJsonRequest("https://api.github.com/user/repos", "POST", body);
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _isCreatingRegistry = false;
                    _createRegistryStatus = $"Failed to create repo ({request.responseCode}): {request.downloadHandler.text}";
                    _createRegistryIsError = true;
                    Repaint();
                    return;
                }

                CreateRepoResponse repo;
                try
                {
                    repo = JsonUtility.FromJson<CreateRepoResponse>(request.downloadHandler.text);
                }
                catch (Exception e)
                {
                    _isCreatingRegistry = false;
                    _createRegistryStatus = $"Repo created, but failed to parse the response: {e.Message}";
                    _createRegistryIsError = true;
                    Repaint();
                    return;
                }

                CreateRegistryFile(repo);
            };
        }

        private void CreateRegistryFile(CreateRepoResponse repo)
        {
            _createRegistryStatus = "Creating registry.json...";
            Repaint();

            const string initialRegistry = "{\n  \"packages\": []\n}\n";
            var content = Convert.ToBase64String(Encoding.UTF8.GetBytes(initialRegistry));
            var body = JsonUtility.ToJson(new CreateFileRequest { message = "Initial registry", content = content });

            var url = $"https://api.github.com/repos/{repo.owner.login}/{repo.name}/contents/registry.json";
            var request = CreateJsonRequest(url, "PUT", body);
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _isCreatingRegistry = false;
                    _createRegistryStatus =
                        $"Repo created, but failed to add registry.json ({request.responseCode}): {request.downloadHandler.text}";
                    _createRegistryIsError = true;
                    Repaint();
                    return;
                }

                FinishCreateRegistry(repo);
            };
        }

        private void FinishCreateRegistry(CreateRepoResponse repo)
        {
            var branch = string.IsNullOrEmpty(repo.default_branch) ? "main" : repo.default_branch;
            var rawUrl = $"https://raw.githubusercontent.com/{repo.owner.login}/{repo.name}/{branch}/registry.json";

            PackageHubSettings.instance.AddRegistry(rawUrl);

            _isCreatingRegistry = false;
            _createRegistryStatus = $"Created {repo.full_name} and added it to your registries.";
            _createRegistryIsError = false;
            _showCreateRegistry = false;
            FetchAllRegistries();
        }

        private void DrawCreatePackageSection()
        {
            _showCreatePackage = EditorGUILayout.Foldout(_showCreatePackage, "Create New Package", true);
            var registryUrls = PackageHubSettings.instance.RegistryUrls;

            if (_showCreatePackage)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        "Scaffolds a new Unity package on disk, publishes it to a new GitHub repo, and adds it " +
                        "to a registry below - the full publish loop, no terminal needed.",
                        EditorStyles.wordWrappedMiniLabel);

                    _newPackageName = EditorGUILayout.TextField(
                        new GUIContent("Package name", "Reverse-domain id, e.g. com.yourname.module"), _newPackageName);
                    _newPackageDisplayName = EditorGUILayout.TextField(
                        new GUIContent("Display name"), _newPackageDisplayName);
                    _newPackageDescription = EditorGUILayout.TextField(
                        new GUIContent("Description"), _newPackageDescription);
                    _newPackageCategory = EditorGUILayout.TextField(
                        new GUIContent("Category", "Optional grouping shown in Package Hub"), _newPackageCategory);

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(
                            new GUIContent("Local folder", "Parent folder where the package files will be created"),
                            GUILayout.Width(EditorGUIUtility.labelWidth));
                        EditorGUILayout.LabelField(string.IsNullOrEmpty(_newPackageFolder) ? "(not set)" : _newPackageFolder);
                        if (GUILayout.Button("Browse...", GUILayout.Width(80)))
                        {
                            var picked = EditorUtility.OpenFolderPanel("Choose parent folder for the new package", "", "");
                            if (!string.IsNullOrEmpty(picked))
                            {
                                _newPackageFolder = picked;
                            }
                        }
                    }

                    _newPackageRepoName = EditorGUILayout.TextField(
                        new GUIContent("GitHub repo name", "Defaults to the last segment of the package name if left blank"),
                        _newPackageRepoName);
                    _newPackagePrivate = EditorGUILayout.Toggle(new GUIContent("Private"), _newPackagePrivate);

                    if (registryUrls.Count == 0)
                    {
                        EditorGUILayout.HelpBox("Configure at least one registry above to publish into.", MessageType.Warning);
                    }
                    else
                    {
                        // '/' in Popup options is treated as a submenu separator (same as MenuItem paths),
                        // so substitute a visually-identical slash that isn't special-cased.
                        var displayOptions = registryUrls.Select(u => u.Replace("/", "∕")).ToArray();
                        _newPackageRegistryIndex = EditorGUILayout.Popup(
                            "Add to registry", Mathf.Clamp(_newPackageRegistryIndex, 0, registryUrls.Count - 1),
                            displayOptions);
                    }

                    if (string.IsNullOrEmpty(GitHubToken))
                    {
                        EditorGUILayout.HelpBox("Add a GitHub token above first.", MessageType.Warning);
                    }

                    var canCreate = !_isCreatingPackage
                                     && !string.IsNullOrEmpty(GitHubToken)
                                     && registryUrls.Count > 0
                                     && PackageNameRegex.IsMatch(_newPackageName.Trim())
                                     && !string.IsNullOrWhiteSpace(_newPackageDisplayName)
                                     && !string.IsNullOrEmpty(_newPackageFolder);

                    GUI.enabled = canCreate;
                    if (GUILayout.Button(_isCreatingPackage ? "Creating..." : "Create Package"))
                    {
                        CreateNewPackage();
                    }

                    GUI.enabled = true;
                }
            }

            DrawStatusMessage(ref _createPackageStatus, _createPackageIsError);

            EditorGUILayout.Space();
        }

        private string DeriveRepoName()
        {
            var trimmed = _newPackageName.Trim();
            var lastDot = trimmed.LastIndexOf('.');
            return lastDot >= 0 ? trimmed.Substring(lastDot + 1) : trimmed;
        }

        private static string DeriveIdentifier(string packageName)
        {
            var segments = packageName.Split('.');
            var parts = new List<string>();
            for (var i = 1; i < segments.Length; i++) // skip the leading "com"
            {
                parts.Add(Capitalize(segments[i]));
            }

            return parts.Count > 0 ? string.Join(".", parts) : Capitalize(packageName);
        }

        private static string Capitalize(string s)
        {
            return string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        private static string NewGuid() => Guid.NewGuid().ToString("N");

        private static string MakeFileMetaText(string guid, string importer)
        {
            return $"fileFormatVersion: 2\nguid: {guid}\n{importer}:\n  externalObjects: {{}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n";
        }

        private static string MakeMonoMetaText(string guid)
        {
            return $"fileFormatVersion: 2\nguid: {guid}\nMonoImporter:\n  externalObjects: {{}}\n  serializedVersion: 2\n  defaultReferences: []\n  executionOrder: 0\n  icon: {{instanceID: 0}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n";
        }

        private static string MakeFolderMetaText(string guid)
        {
            return $"fileFormatVersion: 2\nguid: {guid}\nfolderAsset: yes\nDefaultImporter:\n  externalObjects: {{}}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n";
        }

        private static void ScaffoldPackageFiles(string folder, string packageName, string displayName, string description)
        {
            var runtimeFolder = Path.Combine(folder, "Runtime");
            Directory.CreateDirectory(runtimeFolder);

            var packageJson = JsonUtility.ToJson(new NewPackageJson
            {
                name = packageName,
                displayName = displayName,
                description = description ?? ""
            }, true);
            File.WriteAllText(Path.Combine(folder, "package.json"), packageJson);
            File.WriteAllText(Path.Combine(folder, "package.json.meta"), MakeFileMetaText(NewGuid(), "TextScriptImporter"));

            File.WriteAllText(Path.Combine(folder, "Runtime.meta"), MakeFolderMetaText(NewGuid()));

            var identifier = DeriveIdentifier(packageName);
            var asmdefJson = JsonUtility.ToJson(new AsmdefJson
            {
                name = identifier,
                rootNamespace = identifier
            }, true);
            var asmdefPath = Path.Combine(runtimeFolder, identifier + ".asmdef");
            File.WriteAllText(asmdefPath, asmdefJson);
            File.WriteAllText(asmdefPath + ".meta", MakeFileMetaText(NewGuid(), "AssemblyDefinitionImporter"));

            var scriptName = identifier.Replace(".", "") + "Placeholder";
            var scriptPath = Path.Combine(runtimeFolder, scriptName + ".cs");
            var scriptContent =
                "using UnityEngine;\n\n" +
                $"namespace {identifier}\n" +
                "{\n" +
                $"    public class {scriptName} : MonoBehaviour\n" +
                "    {\n" +
                "    }\n" +
                "}\n";
            File.WriteAllText(scriptPath, scriptContent);
            File.WriteAllText(scriptPath + ".meta", MakeMonoMetaText(NewGuid()));
        }

        private static bool RunGit(string workingDirectory, string arguments, out string output, out string error)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", arguments)
                {
                    WorkingDirectory = workingDirectory,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = System.Diagnostics.Process.Start(psi))
                {
                    output = process.StandardOutput.ReadToEnd();
                    error = process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    return process.ExitCode == 0;
                }
            }
            catch (Exception e)
            {
                output = "";
                error = $"Could not run git: {e.Message} (is git installed and on PATH?)";
                return false;
            }
        }

        private static bool TryParseRawGitHubUrl(string rawUrl, out string owner, out string repo, out string branch, out string path)
        {
            owner = repo = branch = path = null;
            const string prefix = "https://raw.githubusercontent.com/";
            if (!rawUrl.StartsWith(prefix)) return false;

            var rest = rawUrl.Substring(prefix.Length);
            var parts = rest.Split(new[] { '/' }, 4);
            if (parts.Length < 4) return false;

            owner = parts[0];
            repo = parts[1];
            branch = parts[2];
            path = parts[3];
            return true;
        }

        private void CreateNewPackage()
        {
            _isCreatingPackage = true;
            _createPackageIsError = false;
            _createPackageStatus = "Scaffolding package files...";
            Repaint();

            var repoName = string.IsNullOrWhiteSpace(_newPackageRepoName) ? DeriveRepoName() : _newPackageRepoName.Trim();
            var packageFolder = Path.Combine(_newPackageFolder, repoName);
            var packageName = _newPackageName.Trim();
            var displayName = _newPackageDisplayName.Trim();

            try
            {
                if (Directory.Exists(packageFolder) && Directory.GetFileSystemEntries(packageFolder).Length > 0)
                {
                    throw new InvalidOperationException($"Folder already exists and is not empty: {packageFolder}");
                }

                ScaffoldPackageFiles(packageFolder, packageName, displayName, _newPackageDescription);
            }
            catch (Exception e)
            {
                _isCreatingPackage = false;
                _createPackageStatus = $"Failed to scaffold package: {e.Message}";
                _createPackageIsError = true;
                Repaint();
                return;
            }

            _createPackageStatus = "Initializing git repository...";
            Repaint();

            string initErr = "", addErr = "", commitErr = "", branchErr = "";
            if (!RunGit(packageFolder, "init", out _, out initErr) ||
                !RunGit(packageFolder, "add -A", out _, out addErr) ||
                !RunGit(packageFolder, "commit -m \"Initial package scaffold\"", out _, out commitErr) ||
                !RunGit(packageFolder, "branch -M main", out _, out branchErr))
            {
                _isCreatingPackage = false;
                _createPackageStatus = $"Git init failed: {initErr}{addErr}{commitErr}{branchErr}";
                _createPackageIsError = true;
                Repaint();
                return;
            }

            _createPackageStatus = "Creating GitHub repository...";
            Repaint();

            var body = JsonUtility.ToJson(new CreateRepoRequest
            {
                name = repoName,
                description = _newPackageDescription,
                @private = _newPackagePrivate
            });

            var request = CreateJsonRequest("https://api.github.com/user/repos", "POST", body);
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    _isCreatingPackage = false;
                    _createPackageStatus = $"Failed to create repo ({request.responseCode}): {request.downloadHandler.text}";
                    _createPackageIsError = true;
                    Repaint();
                    return;
                }

                CreateRepoResponse repo;
                try
                {
                    repo = JsonUtility.FromJson<CreateRepoResponse>(request.downloadHandler.text);
                }
                catch (Exception e)
                {
                    _isCreatingPackage = false;
                    _createPackageStatus = $"Repo created, but failed to parse the response: {e.Message}";
                    _createPackageIsError = true;
                    Repaint();
                    return;
                }

                PushAndTagPackage(packageFolder, packageName, displayName, repo);
            };
        }

        private void PushAndTagPackage(string packageFolder, string packageName, string displayName, CreateRepoResponse repo)
        {
            _createPackageStatus = "Pushing to GitHub...";
            Repaint();

            var remoteUrl = $"https://github.com/{repo.owner.login}/{repo.name}.git";

            string remoteErr = "", pushErr = "", tagErr = "", pushTagErr = "";
            if (!RunGit(packageFolder, $"remote add origin {remoteUrl}", out _, out remoteErr) ||
                !RunGit(packageFolder, "push -u origin main", out _, out pushErr) ||
                !RunGit(packageFolder, "tag -a v0.1.0 -m \"v0.1.0 - initial package\"", out _, out tagErr) ||
                !RunGit(packageFolder, "push origin v0.1.0", out _, out pushTagErr))
            {
                _isCreatingPackage = false;
                _createPackageStatus = $"Repo created, but push failed: {remoteErr}{pushErr}{tagErr}{pushTagErr}";
                _createPackageIsError = true;
                Repaint();
                return;
            }

            var newEntry = new PackageEntry
            {
                name = packageName,
                displayName = displayName,
                description = _newPackageDescription,
                repoUrl = remoteUrl,
                category = string.IsNullOrWhiteSpace(_newPackageCategory) ? null : _newPackageCategory.Trim()
            };

            var registryUrls = PackageHubSettings.instance.RegistryUrls;
            var targetRegistry = registryUrls[Mathf.Clamp(_newPackageRegistryIndex, 0, registryUrls.Count - 1)];

            _createPackageStatus = "Adding to registry...";
            Repaint();

            AppendToRegistry(targetRegistry, newEntry);
        }

        private void AppendToRegistry(string rawRegistryUrl, PackageEntry newEntry)
        {
            if (!TryParseRawGitHubUrl(rawRegistryUrl, out var owner, out var repo, out var branch, out var path))
            {
                _isCreatingPackage = false;
                _createPackageStatus = "Package repo created and pushed, but couldn't parse the target registry " +
                                        "URL to update it automatically. Add the entry manually.";
                _createPackageIsError = true;
                Repaint();
                return;
            }

            var getUrl = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}?ref={branch}";
            var getRequest = CreateJsonRequest(getUrl, "GET", null);
            var op = getRequest.SendWebRequest();
            op.completed += _ =>
            {
                if (getRequest.result != UnityWebRequest.Result.Success)
                {
                    _isCreatingPackage = false;
                    _createPackageStatus = $"Package repo created and pushed, but failed to read the registry file " +
                                            $"({getRequest.responseCode}). Add the entry manually.";
                    _createPackageIsError = true;
                    Repaint();
                    return;
                }

                ContentsGetResponse existing;
                Registry registry;
                try
                {
                    existing = JsonUtility.FromJson<ContentsGetResponse>(getRequest.downloadHandler.text);
                    var decoded = Encoding.UTF8.GetString(
                        Convert.FromBase64String(existing.content.Replace("\n", "").Replace("\r", "")));
                    registry = JsonUtility.FromJson<Registry>(decoded);
                    if (registry.packages == null) registry.packages = Array.Empty<PackageEntry>();
                }
                catch (Exception e)
                {
                    _isCreatingPackage = false;
                    _createPackageStatus = $"Package repo created and pushed, but failed to parse the registry " +
                                            $"file: {e.Message}. Add the entry manually.";
                    _createPackageIsError = true;
                    Repaint();
                    return;
                }

                var updated = registry.packages.Where(p => p.name != newEntry.name).ToList();
                updated.Add(newEntry);
                registry.packages = updated.ToArray();

                var newContent = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonUtility.ToJson(registry, true)));
                var putBody = JsonUtility.ToJson(new UpdateFileRequest
                {
                    message = $"Add {newEntry.name} to registry",
                    content = newContent,
                    sha = existing.sha
                });

                var putUrl = $"https://api.github.com/repos/{owner}/{repo}/contents/{path}";
                var putRequest = CreateJsonRequest(putUrl, "PUT", putBody);
                var putOp = putRequest.SendWebRequest();
                putOp.completed += __ =>
                {
                    _isCreatingPackage = false;

                    if (putRequest.result != UnityWebRequest.Result.Success)
                    {
                        _createPackageStatus = $"Package repo created and pushed, but failed to update the " +
                                                $"registry ({putRequest.responseCode}). Add the entry manually.";
                        _createPackageIsError = true;
                    }
                    else
                    {
                        _createPackageStatus = $"Created and published {newEntry.name}, added it to the registry.";
                        _createPackageIsError = false;
                        _showCreatePackage = false;
                        FetchAllRegistries();
                    }

                    Repaint();
                };
            };
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
