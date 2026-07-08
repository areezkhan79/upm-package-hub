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
        private const string RegistryUrl = "https://raw.githubusercontent.com/areezkhan79/upm-registry/main/registry.json";

        private static readonly Regex ManifestEntryRegex =
            new Regex("\"(?<name>[^\"]+)\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.Compiled);

        private class PackageUiState
        {
            public bool selected;
            public bool wasInstalled;
            public string[] versions;
            public int selectedVersionIndex = -1;
            public bool isFetchingVersions;
            public string fetchError;
            public string installedVersion;
        }

        private Registry _registry;
        private bool _isLoadingRegistry;
        private string _loadError;
        private readonly Dictionary<string, PackageUiState> _uiState = new Dictionary<string, PackageUiState>();
        private Vector2 _scroll;

        private bool _isApplying;
        private string _applyStatus;
        private bool _applyIsError;
        private AddAndRemoveRequest _pendingRequest;

        [MenuItem("Window/Package Hub")]
        private static void Open()
        {
            var window = GetWindow<PackageHubWindow>();
            window.titleContent = new GUIContent("Package Hub");
            window.minSize = new Vector2(440, 320);
        }

        private void OnEnable()
        {
            FetchRegistry();
        }

        private void OnDisable()
        {
            EditorApplication.update -= PollApplyRequest;
        }

        private void FetchRegistry()
        {
            _isLoadingRegistry = true;
            _loadError = null;
            var request = UnityWebRequest.Get(RegistryUrl);
            var op = request.SendWebRequest();
            op.completed += _ =>
            {
                _isLoadingRegistry = false;

                if (request.result != UnityWebRequest.Result.Success)
                {
                    _loadError = $"Failed to load registry: {request.error}";
                    Repaint();
                    return;
                }

                try
                {
                    _registry = JsonUtility.FromJson<Registry>(request.downloadHandler.text);
                    BuildUiState();
                }
                catch (Exception e)
                {
                    _loadError = $"Failed to parse registry.json: {e.Message}";
                }

                Repaint();
            };
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
                    state.versions = parsed.items?.Select(t => t.name).ToArray() ?? Array.Empty<string>();

                    if (state.versions.Length > 0)
                    {
                        var idx = !string.IsNullOrEmpty(state.installedVersion)
                            ? Array.IndexOf(state.versions, state.installedVersion)
                            : -1;
                        state.selectedVersionIndex = idx >= 0 ? idx : 0;
                    }
                }
                catch (Exception e)
                {
                    state.fetchError = $"Failed to parse tags: {e.Message}";
                }

                Repaint();
            };
        }

        private static (string owner, string repo) ParseOwnerRepo(string repoUrl)
        {
            var trimmed = repoUrl.TrimEnd('/');
            if (trimmed.EndsWith(".git")) trimmed = trimmed.Substring(0, trimmed.Length - 4);
            var parts = trimmed.Split('/');
            return (parts[parts.Length - 2], parts[parts.Length - 1]);
        }

        private void OnGUI()
        {
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Package Hub", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Refresh Registry", GUILayout.Width(130)))
                {
                    FetchRegistry();
                }
            }

            EditorGUILayout.Space();

            if (_isLoadingRegistry)
            {
                EditorGUILayout.HelpBox("Loading registry...", MessageType.Info);
                return;
            }

            if (_loadError != null)
            {
                EditorGUILayout.HelpBox(_loadError, MessageType.Error);
                return;
            }

            if (_registry?.packages == null || _registry.packages.Length == 0)
            {
                EditorGUILayout.HelpBox("No packages found in registry.", MessageType.Warning);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var pkg in _registry.packages)
            {
                DrawPackageRow(pkg, _uiState[pkg.name]);
            }

            EditorGUILayout.EndScrollView();

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUI.enabled = !_isApplying;
                if (GUILayout.Button("Apply to Project", GUILayout.Width(150), GUILayout.Height(28)))
                {
                    ApplyToProject();
                }

                GUI.enabled = true;
            }

            if (_applyStatus != null)
            {
                EditorGUILayout.HelpBox(_applyStatus, _applyIsError ? MessageType.Error : MessageType.Info);
            }
        }

        private void DrawPackageRow(PackageEntry pkg, PackageUiState state)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var wasSelected = state.selected;
                    state.selected = EditorGUILayout.ToggleLeft(
                        pkg.displayName, state.selected, EditorStyles.boldLabel, GUILayout.Width(220));

                    if (state.selected && !wasSelected && state.versions == null && !state.isFetchingVersions)
                    {
                        FetchVersions(pkg, state);
                    }

                    GUILayout.FlexibleSpace();

                    if (!string.IsNullOrEmpty(state.installedVersion))
                    {
                        EditorGUILayout.LabelField($"installed: {state.installedVersion}", GUILayout.Width(160));
                    }
                }

                EditorGUILayout.LabelField(pkg.description, EditorStyles.wordWrappedMiniLabel);

                if (state.selected)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField("Version", GUILayout.Width(60));

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
                            state.selectedVersionIndex =
                                EditorGUILayout.Popup(Mathf.Max(state.selectedVersionIndex, 0), state.versions);
                        }
                        else
                        {
                            EditorGUILayout.LabelField("No tags found for this repo");
                        }
                    }
                }
            }

            EditorGUILayout.Space(4);
        }

        private void ApplyToProject()
        {
            var toAdd = new List<string>();
            var toRemove = new List<string>();

            foreach (var pkg in _registry.packages)
            {
                var state = _uiState[pkg.name];

                if (state.selected)
                {
                    if (state.versions == null || state.versions.Length == 0 || state.selectedVersionIndex < 0)
                    {
                        _applyStatus = $"Cannot add {pkg.displayName}: no version selected or available yet.";
                        _applyIsError = true;
                        return;
                    }

                    var tag = state.versions[state.selectedVersionIndex];
                    if (state.installedVersion == tag) continue;

                    toAdd.Add($"{pkg.repoUrl}#{tag}");
                }
                else if (state.wasInstalled)
                {
                    toRemove.Add(pkg.name);
                }
            }

            if (toAdd.Count == 0 && toRemove.Count == 0)
            {
                _applyStatus = "Nothing to change - selections already match the project.";
                _applyIsError = false;
                return;
            }

            _isApplying = true;
            _applyStatus = "Applying changes...";
            _applyIsError = false;
            _pendingRequest = Client.AddAndRemove(toAdd.ToArray(), toRemove.ToArray());
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
