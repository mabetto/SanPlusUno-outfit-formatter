#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace HINASAKI.Tools
{
    public class SanpurasuOutfitFormatter : EditorWindow
    {
        const string TargetMenuGuid = "568b7e74ff524f24bba18c877dcd04b1";
        const float LilLightMinLimit = 0.05f;
        const float LilMonochromeLighting = 0.5f;
        const int IconSize = 128;
        const int PreviewLayer = 31;
        static string _resourceFolder;
        static string ResourceFolder
        {
            get
            {
                if (_resourceFolder != null) return _resourceFolder;
                var guids = AssetDatabase.FindAssets("t:Script OutfitFormatter",
                    new[] { "Packages/com.hinasaki.outfit-formatter", "Assets" });
                if (guids.Length > 0)
                    _resourceFolder = System.IO.Path.GetDirectoryName(
                        AssetDatabase.GUIDToAssetPath(guids[0])).Replace("\\", "/") + "/Images";
                else
                    _resourceFolder = "Assets/HINASAKI/Editor/Assets/Images";
                return _resourceFolder;
            }
        }
        static string DeleteIconPath => ResourceFolder + "/DELETE.png";
        static string[] FrameIconPaths => new[]
        {
            ResourceFolder + "/FRAME1.png",
            ResourceFolder + "/FRAME2.png",
            ResourceFolder + "/FRAME3.png",
        };

        // Animator グラフ配置定数（SA_COS_A FX.controller に合わせた配置）
        const float GX = 300f;   // ステートのX座標
        const float GY0 = 120f;  // 先頭ステートのY座標（DefaultState）
        const float GYS = 50f;   // ステート間のYステップ
        const float GCX = 50f;   // Entry/AnyState/Exit のX座標

        enum OutfitMode { CosA, CosB, Independent }
        enum ExistingMenuHandling { KeepAsIs, ConvertToFormat, Rebuild }
        enum ParameterType { Bool, Int }
        enum BlendMode { Exclusive, Combine }

        static readonly string[] CosAStandardParams = { "CostumeHead", "CostumeBody", "CostumeSkirt", "カスタム" };

        // ---- 設定ファイル保存・読み込み用 DTO ----
        [Serializable]
        class SavedSlot
        {
            public string label;
            public string parameterName;
            public bool enabled;
            public bool hideOnCastoff;
            public bool hideOnNaked;
            public List<string> rendererPaths = new List<string>(); // prefab root からの相対パス
            public List<string> physBonePaths = new List<string>();
        }
        [Serializable]
        class SavedCamera
        {
            public string key;
            public Vector3 positionOffset;
            public Vector3 rotationEuler;
            public float orthographicSize = 0.5f;
        }
        [Serializable]
        class SavedLayer
        {
            public int layerType; // IconLayerConfig.LayerType as int
            public string texturePath;
            public bool visible = true;
        }
        [Serializable]
        class SavedIconKey
        {
            public string key;
            public List<SavedLayer> layers = new List<SavedLayer>();
        }
        [Serializable]
        class ToolSettings
        {
            public string outputFolder;
            public bool applyToPrefab;
            public bool simpleMode;
            public int simpleOutfitMode; // OutfitMode enum
            public bool cosAPresent;
            public bool cosBPresent;
            public List<SavedSlot> cosASlots = new List<SavedSlot>();
            public List<SavedCamera> cameras = new List<SavedCamera>();
            public List<SavedIconKey> iconKeys = new List<SavedIconKey>();
        }

        [Serializable]
        class CategoryConfig
        {
            public string name = "カテゴリ";
            public int cosAParamIndex = 3; // CosAStandardParams のインデックス（3=カスタム）
            public string parameterName = "";
            public ParameterType parameterType = ParameterType.Bool;
            public BlendMode blendMode = BlendMode.Combine;
            public bool hasCosB = false; // value=1のCOS_Bスロットを挿入するか
            public bool hideOnCastoff = false; // COS_B(CostumeBody=1)時に非表示、キャストオフ(=2)時は表示
            public bool hideOnNaked    = false; // キャストオフ(CostumeBody=2)時に非表示
            public List<PatternConfig> patterns = new List<PatternConfig>();
            public bool foldout = true;

            public string ResolvedParameterName =>
                cosAParamIndex < CosAStandardParams.Length - 1
                    ? CosAStandardParams[cosAParamIndex]
                    : parameterName;
        }

        [Serializable]
        class PatternConfig
        {
            public string label = "パターン";
            public int value = 1;
            public bool isNaked = false; // value=0固定のOFF/NAKEDスロット
            public bool isCosB  = false; // value=1固定のCOS_Bスロット（体のみ）
            public bool menuOnly = false; // メニューエントリのみ生成（FXステートは生成しない）
            public bool isCastoff = false; // キャストオフエントリ（メニュー末尾・DELETEアイコン）
            public List<int> rendererIndices = new List<int>();
            public List<bool> rendererActives = new List<bool>();
            public List<int> physBoneIndices = new List<int>();
            public List<bool> physBoneEnableds = new List<bool>();
            public bool meshFoldout = true;
        }

        class IconLayerConfig
        {
            public enum LayerType { Thumbnail, Frame, Overlay }
            public LayerType type;
            public Texture2D texture;
            public bool visible = true;
        }

        [Serializable]
        class IconCameraConfig
        {
            public Vector3 positionOffset = Vector3.zero;
            public Vector3 rotationEuler = new Vector3(0f, 180f, 0f); // アバター正面を向くデフォルト
            public float orthographicSize = 0.02f;
            public bool foldout = false;
        }

        [Serializable]
        class SerializedCameraEntry { public string key; public IconCameraConfig config; }

        [SerializeField] bool _simpleMode = true;
        [SerializeField] bool _applyToPrefab = true;
        bool _autoLoadedSettings = false; // 自動読み込み通知用（セッション内のみ）
        AnimatorController _lastGeneratedController;

        // COS_Bプレハブのアバタールート基準パス（検出名の表示に使用）
        [SerializeField] string _cosBObjectName = "SA_COS_B";

        // COS_A ポジション存在フラグ（SA_COS_A など。自動検出・手動上書き可）
        [SerializeField] bool _cosAPresent = false;
        bool _cosADetected = false;

        // COS_B ポジション存在フラグ（SA_COS_B などユーザー作成COS_Bも含む。自動検出・手動上書き可）
        [SerializeField] bool _cosBPresent = false;
        bool _cosBDetected = false;

        // 簡単モード用
        [Serializable]
        class SimpleSlot
        {
            public string label;
            public string parameterName;
            public ParameterType paramType;
            public BlendMode blendMode;
            public bool enabled = true;
            public bool hideOnCastoff = false;
            public bool hideOnNaked    = false;
            public List<int> rendererIndices = new List<int>();
            public List<int> physBoneIndices = new List<int>();
        }

        static SimpleSlot[] DefaultCosASlots() => new[]
        {
            new SimpleSlot { label = "Head",  parameterName = "CostumeHead",  paramType = ParameterType.Int,  blendMode = BlendMode.Exclusive },
            new SimpleSlot { label = "Body",  parameterName = "CostumeBody",  paramType = ParameterType.Int,  blendMode = BlendMode.Exclusive },
            new SimpleSlot { label = "Skirt", parameterName = "CostumeSkirt", paramType = ParameterType.Bool, blendMode = BlendMode.Combine  },
        };
        static SimpleSlot[] DefaultCosBSlots() => new[]
        {
            new SimpleSlot { label = "Body", parameterName = "CostumeBody", paramType = ParameterType.Int, blendMode = BlendMode.Exclusive },
        };

        [SerializeField] SimpleSlot[] _simpleCosASlots;
        [SerializeField] SimpleSlot[] _simpleCosBSlots;
        [SerializeField] OutfitMode _simpleOutfitMode = OutfitMode.CosA;
        [SerializeField] string _simpleIndependentParamName = "";
        VRCExpressionsMenu _simpleIndependentInstallTarget;
        [SerializeField] List<int> _simpleIndependentRendererIndices = new List<int>();
        [SerializeField] List<int> _simpleIndependentPhysBoneIndices = new List<int>();

        [SerializeField] GameObject _prefab;
        [SerializeField] string _outputFolder = "Assets/Generated";
        [SerializeField] VRCExpressionsMenu _cosAOutfitMenu;
        bool _hasExistingMenu;
        [SerializeField] ExistingMenuHandling _existingMenuHandling = ExistingMenuHandling.KeepAsIs;

        OutfitMode _mode = OutfitMode.CosA;
        string _independentParameterName = "";
        VRCExpressionsMenu _independentInstallTarget;

        List<CategoryConfig> _categories = new List<CategoryConfig>();
        List<Renderer> _scannedRenderers = new List<Renderer>();
        List<Component> _scannedPhysBones = new List<Component>();

        // key: "{categoryIndex}_{patternIndex}"
        Dictionary<string, List<IconLayerConfig>> _iconLayers = new Dictionary<string, List<IconLayerConfig>>();
        Dictionary<string, IconCameraConfig> _iconCameras = new Dictionary<string, IconCameraConfig>();
        [SerializeField] List<SerializedCameraEntry> _serializedCameras = new List<SerializedCameraEntry>();
        [Serializable] class SerializedIconEntry { public string key; public Texture2D icon; }
        [SerializeField] List<SerializedIconEntry> _serializedIcons = new List<SerializedIconEntry>();
        Dictionary<string, bool> _iconFoldouts = new Dictionary<string, bool>();
        Dictionary<string, Texture2D> _previewTextures = new Dictionary<string, Texture2D>();
        Dictionary<string, Texture2D> _generatedIcons = new Dictionary<string, Texture2D>();
        Dictionary<string, Texture2D> _existingIcons = new Dictionary<string, Texture2D>(); // 既存アイコン直接指定

        bool _previewDirty;
        Vector2 _scroll;
        int _selectedCategoryForPattern = -1;
        int _selectedPatternIndex = -1;

        // シーンビュー編集中のアイコンキー（null = 非アクティブ）
        string _sceneEditKey = null;

        [MenuItem("Tools/HINASAKI/OutfitFormatter")]
        static void Open()
        {
            var window = GetWindow<SanpurasuOutfitFormatter>("OutfitFormatter");
            window.minSize = new Vector2(500, 600);
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            Undo.undoRedoPerformed += OnUndoRedo;

            // スロットが未初期化（初回 or ドメインリロード後の初回）なら既定値で作成
            if (_simpleCosASlots == null || _simpleCosASlots.Length == 0)
                _simpleCosASlots = DefaultCosASlots();
            if (_simpleCosBSlots == null || _simpleCosBSlots.Length == 0)
                _simpleCosBSlots = DefaultCosBSlots();

            // カメラ設定をシリアライズリストから復元
            foreach (var entry in _serializedCameras)
                if (!string.IsNullOrEmpty(entry.key) && entry.config != null)
                    _iconCameras[entry.key] = entry.config;

            // 生成済みアイコンをシリアライズリストから復元（ドメインリロード対策）
            RestoreIconsFromSerializedList();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            Undo.undoRedoPerformed -= OnUndoRedo;
            _sceneEditKey = null;

            // カメラ設定をシリアライズリストに保存
            SyncCamerasToSerializedList();
        }

        void OnLostFocus()
        {
            // フォーカス移動ではシーン編集を終了しない（SceneView操作のために離れることがあるため）
        }

        void OnUndoRedo()
        {
            // Undo/Redo後に _serializedCameras → _iconCameras を再同期
            _iconCameras.Clear();
            foreach (var entry in _serializedCameras)
                if (!string.IsNullOrEmpty(entry.key) && entry.config != null)
                    _iconCameras[entry.key] = entry.config;
            _previewDirty = true;
            Repaint();
        }

        void SyncCamerasToSerializedList()
        {
            _serializedCameras.Clear();
            foreach (var kv in _iconCameras)
                _serializedCameras.Add(new SerializedCameraEntry { key = kv.Key, config = kv.Value });
        }

        void SyncIconsToSerializedList()
        {
            _serializedIcons.Clear();
            foreach (var kv in _generatedIcons)
                if (kv.Value != null)
                    _serializedIcons.Add(new SerializedIconEntry { key = kv.Key, icon = kv.Value });
        }

        void RestoreIconsFromSerializedList()
        {
            foreach (var e in _serializedIcons)
                if (!string.IsNullOrEmpty(e.key) && e.icon != null)
                    _generatedIcons[e.key] = e.icon;
        }

        void AutoSaveSettings()
        {
            if (_prefab == null || string.IsNullOrEmpty(_outputFolder)) return;
            if (_scannedRenderers.Count == 0) return;

            string settingsPath = _outputFolder + "/" + _prefab.name + "_OutfitFormatterSettings.json";
            var s = BuildToolSettings();
            string absPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", settingsPath));
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(absPath));
            System.IO.File.WriteAllText(absPath, JsonUtility.ToJson(s, true), System.Text.Encoding.UTF8);
            AssetDatabase.ImportAsset(settingsPath);
            Debug.Log($"[OutfitFormatter] 設定を自動保存しました: {settingsPath}");
        }

        ToolSettings BuildToolSettings()
        {
            var s = new ToolSettings
            {
                outputFolder     = _outputFolder,
                applyToPrefab    = _applyToPrefab,
                simpleMode       = _simpleMode,
                simpleOutfitMode = (int)_simpleOutfitMode,
                cosAPresent      = _cosAPresent,
                cosBPresent      = _cosBPresent,
            };
            foreach (var slot in _simpleCosASlots)
            {
                var ss = new SavedSlot
                {
                    label         = slot.label,
                    parameterName = slot.parameterName,
                    enabled       = slot.enabled,
                    hideOnCastoff = slot.hideOnCastoff,
                    hideOnNaked   = slot.hideOnNaked,
                };
                foreach (var ri in slot.rendererIndices)
                    if (ri < _scannedRenderers.Count && _scannedRenderers[ri] != null)
                        ss.rendererPaths.Add(GetRelativePath(_scannedRenderers[ri].transform));
                foreach (var bi in slot.physBoneIndices)
                    if (bi < _scannedPhysBones.Count && _scannedPhysBones[bi] != null)
                        ss.physBonePaths.Add(GetRelativePath(_scannedPhysBones[bi].transform));
                s.cosASlots.Add(ss);
            }
            foreach (var kv in _iconCameras)
                s.cameras.Add(new SavedCamera
                {
                    key              = kv.Key,
                    positionOffset   = kv.Value.positionOffset,
                    rotationEuler    = kv.Value.rotationEuler,
                    orthographicSize = kv.Value.orthographicSize,
                });
            foreach (var kv in _iconLayers)
            {
                var sk = new SavedIconKey { key = kv.Key };
                foreach (var layer in kv.Value)
                    sk.layers.Add(new SavedLayer
                    {
                        layerType   = (int)layer.type,
                        texturePath = layer.texture != null ? AssetDatabase.GetAssetPath(layer.texture) : "",
                        visible     = layer.visible,
                    });
                s.iconKeys.Add(sk);
            }
            return s;
        }

        void TryAutoLoadSettings()
        {
            if (_prefab == null) return;
            string folder = ResolveOutputFolder();
            string settingsPath = folder + "/" + _prefab.name + "_OutfitFormatterSettings.json";
            string absPath = System.IO.Path.GetFullPath(
                System.IO.Path.Combine(Application.dataPath, "..", settingsPath));
            if (!System.IO.File.Exists(absPath)) return;

            string json = System.IO.File.ReadAllText(absPath, System.Text.Encoding.UTF8);
            var s = JsonUtility.FromJson<ToolSettings>(json);
            if (s == null) return;

            _outputFolder     = s.outputFolder;
            _applyToPrefab    = s.applyToPrefab;
            _simpleMode       = s.simpleMode;
            _simpleOutfitMode = (OutfitMode)s.simpleOutfitMode;
            _cosAPresent      = s.cosAPresent;
            _cosBPresent      = s.cosBPresent;

            var rPathToIdx = new Dictionary<string, int>();
            for (int i = 0; i < _scannedRenderers.Count; i++)
                if (_scannedRenderers[i] != null)
                    rPathToIdx[GetRelativePath(_scannedRenderers[i].transform)] = i;
            var bPathToIdx = new Dictionary<string, int>();
            for (int i = 0; i < _scannedPhysBones.Count; i++)
                if (_scannedPhysBones[i] != null)
                    bPathToIdx[GetRelativePath(_scannedPhysBones[i].transform)] = i;

            for (int si = 0; si < s.cosASlots.Count && si < _simpleCosASlots.Length; si++)
            {
                var ss   = s.cosASlots[si];
                var slot = _simpleCosASlots[si];
                slot.label         = ss.label;
                slot.enabled       = ss.enabled;
                slot.hideOnCastoff = ss.hideOnCastoff;
                slot.hideOnNaked   = ss.hideOnNaked;
                slot.rendererIndices.Clear();
                foreach (var p in ss.rendererPaths)
                    if (rPathToIdx.TryGetValue(p, out int idx)) slot.rendererIndices.Add(idx);
                slot.physBoneIndices.Clear();
                foreach (var p in ss.physBonePaths)
                    if (bPathToIdx.TryGetValue(p, out int idx)) slot.physBoneIndices.Add(idx);
            }

            foreach (var sc in s.cameras)
            {
                _iconCameras[sc.key] = new IconCameraConfig
                {
                    positionOffset   = sc.positionOffset,
                    rotationEuler    = sc.rotationEuler,
                    orthographicSize = sc.orthographicSize,
                };
            }

            foreach (var sk in s.iconKeys)
            {
                var layers = new List<IconLayerConfig>();
                foreach (var sl in sk.layers)
                    layers.Add(new IconLayerConfig
                    {
                        type    = (IconLayerConfig.LayerType)sl.layerType,
                        texture = string.IsNullOrEmpty(sl.texturePath) ? null
                                  : AssetDatabase.LoadAssetAtPath<Texture2D>(sl.texturePath),
                        visible = sl.visible,
                    });
                _iconLayers[sk.key] = layers;
            }
            _previewDirty = true;
            _autoLoadedSettings = true;

            Repaint();
            Debug.Log($"[OutfitFormatter] 設定を自動読み込みしました: {settingsPath}");
        }

        void OnSceneGUI(SceneView sv)
        {
            if (_sceneEditKey == null || _prefab == null) return;
            var camCfg = GetCameraConfig(_sceneEditKey);

            // 対象レンダラーのboundsをプレハブから計算
            var bounds = new Bounds(_prefab.transform.position, Vector3.one * 0.1f);
            bool first = true;
            var allR = _prefab.GetComponentsInChildren<Renderer>(true);
            var targetIdx = GetRendererIndicesForKey(_sceneEditKey);
            IEnumerable<Renderer> targets = (targetIdx != null && targetIdx.Count > 0)
                ? targetIdx
                    .Where(i => i < _scannedRenderers.Count && _scannedRenderers[i] != null)
                    .Select(i => {
                        string p = GetRelativePath(_scannedRenderers[i].transform);
                        var t = string.IsNullOrEmpty(p) ? _prefab.transform : _prefab.transform.Find(p);
                        return t?.GetComponent<Renderer>();
                    })
                    .Where(r => r != null)
                : allR;
            foreach (var r in targets)
            {
                if (r == null) continue;
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }

            var rot = Quaternion.Euler(camCfg.rotationEuler);
            var basePos = bounds.center - rot * Vector3.forward * (bounds.extents.magnitude + 1f);
            // positionOffset.z をZ軸ドラッグ量の蓄積に使う（RenderIconではXYのみ使用）
            var handlePos = basePos + rot * camCfg.positionOffset;

            EditorGUI.BeginChangeCheck();
            var newPos = Handles.PositionHandle(handlePos, rot);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(this, "アイコンカメラ設定変更");
                SyncCamerasToSerializedList();
                var localDelta = Quaternion.Inverse(rot) * (newPos - handlePos);
                camCfg.positionOffset += new Vector3(
                    Mathf.Abs(localDelta.x) > 1e-5f ? localDelta.x : 0f,
                    Mathf.Abs(localDelta.y) > 1e-5f ? localDelta.y : 0f,
                    Mathf.Abs(localDelta.z) > 1e-5f ? localDelta.z : 0f);
                // Z前進=ズームイン、Z後退=ズームアウト
                camCfg.orthographicSize = Mathf.Clamp(
                    camCfg.orthographicSize - localDelta.z * 0.01f, 0.001f, 5f);
                SyncCamerasToSerializedList();
                _previewDirty = true;
                Repaint();
            }


            // カメラビュー矩形（ortho撮影範囲）
            float size = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.2f
                * Mathf.Max(camCfg.orthographicSize / 0.1f, 0.01f);
            Handles.color = new Color(1f, 0.8f, 0f, 0.9f);
            var right = rot * Vector3.right * size;
            var up    = rot * Vector3.up    * size;
            // 撮影面（カメラの少し前）
            var c = handlePos + rot * Vector3.forward * (bounds.extents.magnitude + 1f);
            Handles.DrawLine(c - right - up, c + right - up);
            Handles.DrawLine(c + right - up, c + right + up);
            Handles.DrawLine(c + right + up, c - right + up);
            Handles.DrawLine(c - right + up, c - right - up);
            // 視錐台の線（カメラ位置 → 撮影面4隅）
            Handles.color = new Color(1f, 0.8f, 0f, 0.4f);
            Handles.DrawLine(handlePos, c - right - up);
            Handles.DrawLine(handlePos, c + right - up);
            Handles.DrawLine(handlePos, c + right + up);
            Handles.DrawLine(handlePos, c - right + up);

            // ラベル（スロット名をキーから引く）
            string slotLabel = _sceneEditKey;
            if (_simpleMode && int.TryParse(_sceneEditKey.Split('_')[0], out int si))
            {
                var slots = _simpleOutfitMode == OutfitMode.CosA ? _simpleCosASlots : _simpleCosBSlots;
                if (si < slots.Length) slotLabel = slots[si].label;
            }
            var labelStyle = new GUIStyle(EditorStyles.boldLabel);
            labelStyle.normal.textColor = new Color(1f, 0.9f, 0.2f);
            Handles.Label(handlePos + rot * Vector3.up * (size + 0.06f),
                $"[アイコンカメラ] {slotLabel}  XY=移動  Z=ズーム", labelStyle);
        }

        void OnInspectorUpdate()
        {
            // シーン配置状態の変化（配置・除去）を拾うため定期的に再検出
            if (_prefab == null) return;
            bool wasA = _cosADetected, wasB = _cosBDetected;
            DetectCosA();
            DetectCosB();
            if (_cosADetected != wasA || _cosBDetected != wasB) Repaint();
        }

        void OnGUI()
        {
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            // モード切替タブ
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_simpleMode, "簡単モード", "Button")) _simpleMode = true;
            if (GUILayout.Toggle(!_simpleMode, "詳細モード", "Button")) _simpleMode = false;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(8);

            if (_simpleMode)
            {
                DrawSimpleMode();
            }
            else
            {
                DrawPrefabSection();
                EditorGUILayout.Space(8);
                DrawModeSection();
                EditorGUILayout.Space(8);
                DrawCategorySection();
                EditorGUILayout.Space(8);
                DrawIconSection();
                EditorGUILayout.Space(8);
                DrawExecuteSection();
            }

            EditorGUILayout.EndScrollView();

            if (_previewDirty)
            {
                _previewDirty = false;
                RegenerateAllPreviews();
                Repaint();
            }
        }

        // ---------------- 簡単モード ----------------

        void DrawSimpleMode()
        {
            EditorGUILayout.Space(4);

            // 自動読み込み通知
            if (_autoLoadedSettings)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.HelpBox("前回の設定を自動で読み込みました。内容を確認してから「生成する」を実行してください。", MessageType.Info);
                if (GUILayout.Button("×", GUILayout.Width(24), GUILayout.Height(38)))
                    _autoLoadedSettings = false;
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4);
            }

            // 1. プレハブ
            EditorGUILayout.LabelField("1. 衣装プレハブ", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            _prefab = (GameObject)EditorGUILayout.ObjectField("衣装プレハブ", _prefab, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) OnPrefabChanged();
            if (_prefab == null)
            {
                EditorGUILayout.HelpBox("ヒエラルキーまたはProjectウィンドウから衣装プレハブをここにドロップしてください。", MessageType.Warning);
            }

            if (_hasExistingMenu)
            {
                string existingMenuDesc = _existingMenuHandling switch {
                    ExistingMenuHandling.KeepAsIs        => "メニューには手を加えず、アニメーターと MA Parameters のみ更新します。他ツールや手動で組んだメニューを崩したくない場合に選択してください。",
                    ExistingMenuHandling.ConvertToFormat => "既存の MA Menu Installer を流用し、インストール先を 3+1 OutfitMenu に切り替えます。",
                    ExistingMenuHandling.Rebuild         => "メニューアセットを再生成して MA Menu Installer の参照を上書きします。このツールで何度も設定し直す場合はこちらを選択してください。",
                    _ => ""
                };
                EditorGUILayout.HelpBox("既存の MA メニュー設定が見つかりました。\n" + existingMenuDesc, MessageType.Info);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Toggle(_existingMenuHandling == ExistingMenuHandling.KeepAsIs,
                    new GUIContent("メニューはそのまま（アニメーターのみ更新）",
                        "他のツールや手動で組んだメニューを崩したくない場合に選択します。\nアニメーターと MA Parameters の更新のみ行い、メニューには一切手を加えません。"), "Button"))
                    _existingMenuHandling = ExistingMenuHandling.KeepAsIs;
                if (GUILayout.Toggle(_existingMenuHandling == ExistingMenuHandling.ConvertToFormat,
                    new GUIContent("3+1形式に付け替える",
                        "既存の MA Menu Installer を流用し、インストール先を 3+1 の OutfitMenu に切り替えます。\nメニューの中身（コントロール）はそのまま残ります。"), "Button"))
                    _existingMenuHandling = ExistingMenuHandling.ConvertToFormat;
                if (GUILayout.Toggle(_existingMenuHandling == ExistingMenuHandling.Rebuild,
                    new GUIContent("メニューを上書き再生成",
                        "このツールの設定をもとにメニューアセットを作り直し、MA Menu Installer の参照を上書きします。\n何度も設定し直す場合や初回セットアップに使用してください。"), "Button"))
                    _existingMenuHandling = ExistingMenuHandling.Rebuild;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(8);

            // 2. モード
            EditorGUILayout.LabelField("2. モードを選ぶ", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_simpleOutfitMode == OutfitMode.CosA,       "COS_Aポジション", "Button")) _simpleOutfitMode = OutfitMode.CosA;
            if (GUILayout.Toggle(_simpleOutfitMode == OutfitMode.CosB,       "COS_Bポジション", "Button")) _simpleOutfitMode = OutfitMode.CosB;
            if (GUILayout.Toggle(_simpleOutfitMode == OutfitMode.Independent, "独立",           "Button")) _simpleOutfitMode = OutfitMode.Independent;
            EditorGUILayout.EndHorizontal();
            switch (_simpleOutfitMode)
            {
                case OutfitMode.CosA:
                    EditorGUILayout.HelpBox("3+1のOutfitMenuにメニューを追加し、COS_Aパラメーターで制御します。", MessageType.Info);
                    _cosAOutfitMenu = (VRCExpressionsMenu)EditorGUILayout.ObjectField(
                        new GUIContent("インストール先 OutfitMenu", "COS_Aメニューをインストールする VRCExpressionsMenu アセット"),
                        _cosAOutfitMenu, typeof(VRCExpressionsMenu), false);
                    break;
                case OutfitMode.CosB:
                    EditorGUILayout.HelpBox("メニュー生成なし。アニメーターとMA Parametersのみ生成します。", MessageType.Info); break;
                case OutfitMode.Independent:
                    EditorGUILayout.HelpBox("独自パラメーターで動作する独立した衣装として生成します。", MessageType.Info); break;
            }

            EditorGUILayout.Space(8);

            // 3. メッシュ選択
            EditorGUILayout.LabelField("3. メッシュを割り当てる", EditorStyles.boldLabel);

            if (_simpleOutfitMode == OutfitMode.CosA)
            {
                DrawCosADetectionToggle();
                EditorGUILayout.Space(4);

                if (_prefab == null)
                {
                    EditorGUILayout.HelpBox("プレハブを設定すると、メッシュの割り当てができるようになります。", MessageType.None);
                }
                else
                {
                    foreach (var slot in _simpleCosASlots)
                    {
                        bool autoBody = slot.parameterName == "CostumeBody";
                        bool hasNoMesh = slot.enabled && !autoBody && slot.rendererIndices.Count == 0;

                        EditorGUILayout.BeginVertical("box");
                        EditorGUILayout.BeginHorizontal();
                        slot.enabled = GUILayout.Toggle(slot.enabled, slot.enabled ? "使用する" : "スキップ",
                            GUILayout.Width(72));
                        slot.label = EditorGUILayout.TextField(slot.label);
                        GUILayout.Label("→ " + slot.parameterName, EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                        if (hasNoMesh)
                        {
                            var warnStyle = new GUIStyle(EditorStyles.miniLabel);
                            warnStyle.normal.textColor = new Color(1f, 0.7f, 0f);
                            GUILayout.Label("⚠ メッシュ未選択", warnStyle, GUILayout.ExpandWidth(false));
                        }
                        EditorGUILayout.EndHorizontal();
                        if (slot.enabled && !autoBody)
                        {
                            EditorGUI.indentLevel++;
                            if (!_cosAPresent)
                            {
                                slot.hideOnCastoff = EditorGUILayout.ToggleLeft(
                                    new GUIContent("COS_B時に非表示（キャストオフ時は表示）",
                                        "CostumeBody=1（温泉タオル）時にこのスロットを非表示にします。CostumeBody=2（キャストオフ）時は表示したままにします。"),
                                    slot.hideOnCastoff);
                                slot.hideOnNaked = EditorGUILayout.ToggleLeft(
                                    new GUIContent("キャストオフ時に非表示",
                                        "CostumeBody=2（キャストオフ）時にこのスロットを非表示にします。"),
                                    slot.hideOnNaked);
                            }
                            DrawSimpleSlotContent(slot);
                            EditorGUI.indentLevel--;
                        }
                        else if (slot.enabled && autoBody)
                        {
                            EditorGUI.indentLevel++;
                            EditorGUILayout.LabelField("プレハブ内の全レンダラー・PhysBoneを自動で使用します", EditorStyles.miniLabel);
                            EditorGUI.indentLevel--;
                        }
                        EditorGUILayout.EndVertical();
                    }
                }
            }
            else if (_simpleOutfitMode == OutfitMode.CosB)
            {
                EditorGUILayout.HelpBox(
                    "COS_Bポジションモードは、アバターにすでに COS_Bポジションの衣装が存在する場合は使用できません。",
                    MessageType.Info);
                DrawCosADetectionToggle(showCosBRow: false);
                if (_cosBDetected)
                    EditorGUILayout.HelpBox(
                        "COS_Bポジションのオブジェクトが検出されました。既存の COS_B 衣装と競合するため生成できません。",
                        MessageType.Warning);
                EditorGUILayout.Space(4);
                EditorGUILayout.HelpBox("プレハブ内の全レンダラー・PhysBoneを自動で使用します。", MessageType.None);
            }
            else // Independent
            {
                _simpleIndependentParamName = EditorGUILayout.TextField("パラメーター名", _simpleIndependentParamName);
                _simpleIndependentInstallTarget = (VRCExpressionsMenu)EditorGUILayout.ObjectField(
                    "インストール先メニュー", _simpleIndependentInstallTarget, typeof(VRCExpressionsMenu), false);
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.LabelField("処理対象オブジェクト（チェック＝対象に含める）", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                for (int ri = 0; ri < _scannedRenderers.Count; ri++)
                {
                    var r = _scannedRenderers[ri];
                    if (r == null) continue;
                    bool on = _simpleIndependentRendererIndices.Contains(ri);
                    string label = GetShortPath(r.transform);
                    if (!r.gameObject.activeSelf) label += " (非表示)";
                    var content = new GUIContent(label, GetRelativePath(r.transform));
                    bool newOn = EditorGUILayout.ToggleLeft(content, on);
                    if (newOn != on)
                    {
                        if (newOn) _simpleIndependentRendererIndices.Add(ri);
                        else _simpleIndependentRendererIndices.Remove(ri);
                    }
                }
                if (_scannedRenderers.Count == 0)
                    EditorGUILayout.LabelField("← まず衣装プレハブを設定してください", EditorStyles.miniLabel);
                EditorGUI.indentLevel--;

                // PhysBone（独立モード）
                if (_scannedPhysBones.Count > 0)
                {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField("処理対象PhysBone（チェック＝対象に含める）", EditorStyles.miniBoldLabel);
                    EditorGUI.indentLevel++;
                    for (int bi = 0; bi < _scannedPhysBones.Count; bi++)
                    {
                        var pb = _scannedPhysBones[bi];
                        if (pb == null) continue;
                        // 独立モードでは _simpleCosBSlots[0] と同じように一時スロットで管理できないため
                        // 簡略化: _simpleIndependentRendererIndices を拡張せず、CosB スロットのフィールドを借用
                        // → 独自フィールド _simpleIndependentPhysBoneIndices を使う
                        bool on = _simpleIndependentPhysBoneIndices.Contains(bi);
                        var content = new GUIContent(GetShortPath(pb.transform), GetRelativePath(pb.transform));
                        bool newOn = EditorGUILayout.ToggleLeft(content, on);
                        if (newOn != on)
                        {
                            if (newOn) _simpleIndependentPhysBoneIndices.Add(bi);
                            else _simpleIndependentPhysBoneIndices.Remove(bi);
                        }
                    }
                    EditorGUI.indentLevel--;
                }
                EditorGUILayout.EndVertical();
            }

            // COS_B 連携設定（COS_A + Body スロット使用時のみ）
            bool anyBodyCosA = _simpleOutfitMode == OutfitMode.CosA &&
                _simpleCosASlots.Any(s => s.enabled && s.parameterName == "CostumeBody" && s.rendererIndices.Count > 0);
            if (anyBodyCosA && _cosAPresent)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("3a. COS_B 連携設定（COS_A ポジション共存時）", EditorStyles.boldLabel);
                if (_cosBDetected)
                    EditorGUILayout.HelpBox($"COS_B 検出: {_cosBObjectName}", MessageType.None);
                else
                    EditorGUILayout.HelpBox("COS_B ポジションがシーンに配置されると自動検出されます。", MessageType.Warning);
            }

            // SA_COS_B 連携（置き換えモード時）
            bool anyBodyReplacement = _simpleOutfitMode == OutfitMode.CosA && !_cosAPresent &&
                _simpleCosASlots.Any(s => s.enabled && s.parameterName == "CostumeBody" && s.rendererIndices.Count > 0);
            if (anyBodyReplacement)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.LabelField("3b. COS_B 連携設定（SA_COS_A 置き換えモード）", EditorStyles.boldLabel);
                string cosBStatus = _cosBDetected ? "● 検出済み" : "○ 未検出";
                var cosBStyle = new GUIStyle(EditorStyles.miniLabel);
                cosBStyle.normal.textColor = _cosBDetected ? new Color(0.2f, 0.7f, 0.2f) : new Color(0.8f, 0.5f, 0.1f);
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(cosBStatus, cosBStyle, GUILayout.Width(80));
                _cosBPresent = EditorGUILayout.ToggleLeft(
                    new GUIContent("COS_B ポジションがある",
                        "SA_COS_B などユーザー作成の COS_B 枠がある場合にオンにします。CostumeBody=1（COS_B）・=2（NAKED）ステートを FX レイヤーに明示的に生成します。"),
                    _cosBPresent);
                EditorGUILayout.EndHorizontal();
                if (_cosBPresent && _cosBDetected)
                    EditorGUILayout.HelpBox($"検出: {_cosBObjectName}", MessageType.None);
            }

            EditorGUILayout.Space(8);

            // 出力設定
            EditorGUILayout.LabelField("4. 出力設定", EditorStyles.boldLabel);
            _applyToPrefab = EditorGUILayout.ToggleLeft(
                new GUIContent("生成後にプレハブへ自動適用する",
                    "オンにすると、プレハブと同じフォルダに「プレハブ名_generated」フォルダを作成し、MA コンポーネントを自動でアタッチします。"),
                _applyToPrefab);
            if (_applyToPrefab)
            {
                string resolved = _prefab != null ? ResolveOutputFolder() : "（プレハブを設定してください）";
                var style = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
                EditorGUILayout.LabelField("出力先: " + resolved, style);
            }
            else
            {
                EditorGUILayout.BeginHorizontal();
                _outputFolder = EditorGUILayout.TextField("出力フォルダ", _outputFolder);
                if (GUILayout.Button("選択", GUILayout.Width(50)))
                {
                    var sel = EditorUtility.OpenFolderPanel("出力先を選択", "Assets", "");
                    if (!string.IsNullOrEmpty(sel) && sel.StartsWith(Application.dataPath))
                        _outputFolder = "Assets" + sel.Substring(Application.dataPath.Length);
                }
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.Space(8);

            // アイコン設定（COS_Bはメニュー生成なしのため不要）
            if (_simpleOutfitMode != OutfitMode.CosB)
            {
                DrawIconSection();
                EditorGUILayout.Space(4);
            }

            // 生成前バリデーション
            string validationError = GetSimpleValidationError();
            if (!string.IsNullOrEmpty(validationError))
                EditorGUILayout.HelpBox(validationError, MessageType.Warning);

            GUI.enabled = string.IsNullOrEmpty(validationError);
            if (GUILayout.Button("生成する", GUILayout.Height(40)))
                ExecuteSimpleMode();
            GUI.enabled = true;
        }

        string GetSimpleValidationError()
        {
            if (_prefab == null)
                return "衣装プレハブが設定されていません。";

            if (_simpleOutfitMode == OutfitMode.CosA)
            {
                bool hasAny = false;
                foreach (var slot in _simpleCosASlots)
                {
                    if (!slot.enabled) continue;
                    bool autoBody = slot.parameterName == "CostumeBody";
                    if (autoBody || slot.rendererIndices.Count > 0) { hasAny = true; break; }
                }
                if (!hasAny)
                    return "「使用する」にチェックの入ったスロットにメッシュが1つも割り当てられていません。";
            }
            else if (_simpleOutfitMode == OutfitMode.CosB)
            {
                if (_cosBDetected)
                    return "COS_Bポジションのオブジェクトが検出されています。既存の COS_B 衣装と共存できないため生成できません。";
            }
            else // Independent
            {
                if (string.IsNullOrEmpty(_simpleIndependentParamName))
                    return "パラメーター名を入力してください。";
                if (_simpleIndependentRendererIndices.Count == 0)
                    return "処理対象のオブジェクトが1つも選択されていません。";
            }
            return null;
        }

        void DrawSimpleSlotContent(SimpleSlot slot)
        {
            EditorGUILayout.LabelField("処理対象オブジェクト（チェック＝対象に含める）", EditorStyles.miniBoldLabel);
            for (int ri = 0; ri < _scannedRenderers.Count; ri++)
            {
                var r = _scannedRenderers[ri];
                if (r == null) continue;
                bool on = slot.rendererIndices.Contains(ri);
                string slotLabel = GetShortPath(r.transform);
                if (!r.gameObject.activeSelf) slotLabel += " (非表示)";
                var content = new GUIContent(slotLabel, GetRelativePath(r.transform));
                bool newOn = EditorGUILayout.ToggleLeft(content, on);
                if (newOn != on)
                {
                    if (newOn)
                    {
                        slot.rendererIndices.Add(ri);
                        AutoSelectPhysBonesForRenderer(slot, ri);
                    }
                    else
                    {
                        slot.rendererIndices.Remove(ri);
                        RecalculatePhysBonesForSlot(slot);
                    }
                }
            }
            if (_scannedRenderers.Count == 0)
                EditorGUILayout.LabelField("← まず衣装プレハブを設定してください", EditorStyles.miniLabel);

            // PhysBone checklist
            if (_scannedPhysBones.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("処理対象PhysBone（チェック＝対象に含める）", EditorStyles.miniBoldLabel);
                for (int bi = 0; bi < _scannedPhysBones.Count; bi++)
                {
                    var pb = _scannedPhysBones[bi];
                    if (pb == null) continue;
                    int idx = slot.physBoneIndices.IndexOf(bi);
                    bool on = idx >= 0;
                    EditorGUILayout.BeginHorizontal();
                    var pbContent = new GUIContent(GetShortPath(pb.transform), GetRelativePath(pb.transform));
                    bool newOn = EditorGUILayout.ToggleLeft(pbContent, on);
                    if (newOn != on)
                    {
                        if (newOn) slot.physBoneIndices.Add(bi);
                        else slot.physBoneIndices.Remove(bi);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
        }

        void AutoSelectPhysBonesForRenderer(SimpleSlot slot, int rendererIndex)
        {
            if (rendererIndex >= _scannedRenderers.Count) return;
            var smr = _scannedRenderers[rendererIndex] as SkinnedMeshRenderer;
            if (smr == null || smr.sharedMesh == null) return;

            // smr.bones はウェイトゼロのボーンも全て含む。
            // 実際にウェイトが乗っているボーンだけに絞らないと全 PhysBone が誤選択される。
            var allWeights = smr.sharedMesh.GetAllBoneWeights();
            var weightedIndices = new HashSet<int>();
            foreach (var w in allWeights)
                if (w.weight > 0.001f) weightedIndices.Add(w.boneIndex);
            if (weightedIndices.Count == 0) return;

            var bones = smr.bones;
            var boneSet = new HashSet<Transform>(
                weightedIndices.Where(i => i < bones.Length && bones[i] != null).Select(i => bones[i]));

            // PhysBone は別の空 GameObject にまとめられ rootTransform でボーンを指定している構造。
            // rootTransform（指定ボーン）以下の階層に SMR の bones が含まれるか下方向で検索する。
            for (int bi = 0; bi < _scannedPhysBones.Count; bi++)
            {
                var pb = _scannedPhysBones[bi];
                if (pb == null) continue;
                var pbRoot = GetPhysBoneRootTransform(pb);
                if (pbRoot == null) continue;
                if (ContainsAnyBone(pbRoot, boneSet))
                {
                    if (!slot.physBoneIndices.Contains(bi))
                        slot.physBoneIndices.Add(bi);
                }
            }
        }

        void RecalculatePhysBonesForSlot(SimpleSlot slot)
        {
            // 手動で追加されたものは保持するため、自動選択分のみ再計算する。
            // 方針: いったん全クリアして残りの全チェック済みレンダラーから再構築。
            // ただし手動操作（自動とは異なるチェック状態）を保持する手段がないため
            // シンプルに全クリア → 全レンダラーで再オート選択。
            slot.physBoneIndices.Clear();
            foreach (var ri in slot.rendererIndices)
                AutoSelectPhysBonesForRenderer(slot, ri);
        }

        void AutoSelectPhysBonesForPattern(PatternConfig pat, int rendererIndex)
        {
            if (rendererIndex >= _scannedRenderers.Count) return;
            var smr = _scannedRenderers[rendererIndex] as SkinnedMeshRenderer;
            if (smr == null || smr.sharedMesh == null) return;
            var allWeights = smr.sharedMesh.GetAllBoneWeights();
            var weightedIndices = new HashSet<int>();
            foreach (var w in allWeights)
                if (w.weight > 0.001f) weightedIndices.Add(w.boneIndex);
            if (weightedIndices.Count == 0) return;
            var bones = smr.bones;
            var boneSet = new HashSet<Transform>(
                weightedIndices.Where(i => i < bones.Length && bones[i] != null).Select(i => bones[i]));
            for (int bi = 0; bi < _scannedPhysBones.Count; bi++)
            {
                var pb = _scannedPhysBones[bi];
                if (pb == null) continue;
                var pbRoot = GetPhysBoneRootTransform(pb);
                if (pbRoot == null) continue;
                if (ContainsAnyBone(pbRoot, boneSet))
                {
                    if (!pat.physBoneIndices.Contains(bi))
                    {
                        pat.physBoneIndices.Add(bi);
                        pat.physBoneEnableds.Add(true);
                    }
                }
            }
        }

        void RecalculatePhysBonesForPattern(PatternConfig pat)
        {
            pat.physBoneIndices.Clear();
            pat.physBoneEnableds.Clear();
            foreach (var ri in pat.rendererIndices)
                AutoSelectPhysBonesForPattern(pat, ri);
        }

        Transform GetPhysBoneRootTransform(Component pb)
        {
            var so = new SerializedObject(pb);
            // VRCPhysBoneBase のフィールド名を優先順に試す
            foreach (var propName in new[] { "rootTransform", "m_RootTransform", "root" })
            {
                var prop = so.FindProperty(propName);
                if (prop != null && prop.objectReferenceValue is Transform t && t != null)
                    return t;
            }
            // rootTransform 未指定の場合はコンポーネント自体の Transform がルート
            return pb.transform;
        }

        bool ContainsAnyBone(Transform root, HashSet<Transform> boneSet)
        {
            if (boneSet.Contains(root)) return true;
            foreach (Transform child in root)
                if (ContainsAnyBone(child, boneSet)) return true;
            return false;
        }

        void ApplyToPrefab()
        {
            if (_prefab == null || _lastGeneratedController == null) return;

            // MA Merge Animator
            var mergeAnimType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => (t.FullName ?? "").Contains("ModularAvatarMergeAnimator") && typeof(Component).IsAssignableFrom(t));
            if (mergeAnimType != null)
            {
                Component existing = _prefab.GetComponent(mergeAnimType)
                    ?? _prefab.AddComponent(mergeAnimType);
                var so = new SerializedObject(existing);
                so.FindProperty("animator").objectReferenceValue = _lastGeneratedController;
                var layerTypeProp = so.FindProperty("layerType");
                if (layerTypeProp != null) layerTypeProp.intValue = 5; // FX
                var pathModeProp = so.FindProperty("pathMode");
                if (pathModeProp != null) pathModeProp.intValue = 0; // Relative
                var matchWDProp = so.FindProperty("matchAvatarWriteDefaults");
                if (matchWDProp != null) matchWDProp.boolValue = true;
                var deleteAnimProp = so.FindProperty("deleteAttachedAnimator");
                if (deleteAnimProp != null) deleteAnimProp.boolValue = true;
                so.ApplyModifiedProperties();
                EditorUtility.SetDirty(existing);
            }

            // MA Parameters
            var maParamsType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => (t.FullName ?? "").Contains("ModularAvatarParameters") && typeof(Component).IsAssignableFrom(t));
            if (maParamsType != null)
            {
                Component existing = _prefab.GetComponent(maParamsType)
                    ?? _prefab.AddComponent(maParamsType);
                var so = new SerializedObject(existing);
                var paramsProp = so.FindProperty("parameters");
                if (paramsProp != null)
                {
                    // OutfitFormatterが管理するパラメーター名セット
                    var ourParams = _categories
                        .Select(c => c.ResolvedParameterName)
                        .Where(n => !string.IsNullOrEmpty(n))
                        .ToHashSet();

                    // 既存エントリのうちOutfitFormatter管理外のものを保持し、管理分を末尾で上書き/追加
                    // まず管理分のインデックスを収集（nameOrPrefixで照合）
                    var existingOurIndices = new Dictionary<string, int>();
                    for (int i = 0; i < paramsProp.arraySize; i++)
                    {
                        var name = paramsProp.GetArrayElementAtIndex(i)
                            .FindPropertyRelative("nameOrPrefix")?.stringValue ?? "";
                        if (ourParams.Contains(name))
                            existingOurIndices[name] = i;
                    }

                    foreach (var cat in _categories)
                    {
                        var resolved = cat.ResolvedParameterName;
                        if (string.IsNullOrEmpty(resolved)) continue;
                        int idx;
                        if (existingOurIndices.TryGetValue(resolved, out idx))
                        {
                            // 既存エントリを上書き
                        }
                        else
                        {
                            idx = paramsProp.arraySize;
                            paramsProp.InsertArrayElementAtIndex(idx);
                        }
                        var elem = paramsProp.GetArrayElementAtIndex(idx);
                        elem.FindPropertyRelative("nameOrPrefix").stringValue = resolved;
                        var typeProp = elem.FindPropertyRelative("syncType");
                        if (typeProp != null) typeProp.intValue = cat.parameterType == ParameterType.Int ? 1 : 3;
                        elem.FindPropertyRelative("saved").boolValue = false;
                        elem.FindPropertyRelative("defaultValue").floatValue = 0f;
                    }
                    so.ApplyModifiedProperties();
                    EditorUtility.SetDirty(existing);
                }
            }



            // SA_COS_A共存モード: prefab内Rendererをデフォルト無効化（FXレイヤーが有効化するまで非表示）
            if (_simpleOutfitMode == OutfitMode.CosA && _cosAPresent)
            {
                foreach (var r in _prefab.GetComponentsInChildren<Renderer>(true))
                    r.enabled = false;
                EditorUtility.SetDirty(_prefab);
            }

            AssetDatabase.SaveAssets();
            // シーンに追加した MA コンポーネントを永続化するためシーンも保存
            var scene = _prefab.scene;
            if (scene.IsValid())
                EditorSceneManager.SaveScene(scene);
            EditorUtility.DisplayDialog("完了", "Prefabにコンポーネントを適用しました。", "OK");
        }

        void ExecuteSimpleMode()
        {
            if (_scannedRenderers.Count == 0) ScanPrefab();

            _categories.Clear();
            _mode = _simpleOutfitMode;

            if (_simpleOutfitMode == OutfitMode.CosA)
            {
                foreach (var slot in _simpleCosASlots)
                {
                    if (!slot.enabled) continue;
                    bool autoBody = slot.parameterName == "CostumeBody";
                    if (!autoBody && slot.rendererIndices.Count == 0) continue;
                    _categories.Add(BuildSimpleCategory(slot));
                }
            }
            else if (_simpleOutfitMode == OutfitMode.CosB)
            {
                var slot = _simpleCosBSlots[0];
                _categories.Add(BuildSimpleCategory(slot, isCosB: true));
            }
            else // Independent
            {
                if (string.IsNullOrEmpty(_simpleIndependentParamName))
                {
                    EditorUtility.DisplayDialog("エラー", "パラメーター名を入力してください。", "OK");
                    return;
                }
                _independentParameterName = _simpleIndependentParamName;
                _independentInstallTarget = _simpleIndependentInstallTarget;

                var cat = new CategoryConfig
                {
                    name = _simpleIndependentParamName,
                    parameterName = _simpleIndependentParamName,
                    cosAParamIndex = CosAStandardParams.Length - 1,
                    parameterType = ParameterType.Bool,
                    blendMode = BlendMode.Combine
                };
                var pat = new PatternConfig { label = "ON", value = 1 };
                pat.rendererIndices.AddRange(_simpleIndependentRendererIndices);
                pat.rendererActives.AddRange(_simpleIndependentRendererIndices.Select(_ => true));
                pat.physBoneIndices.AddRange(_simpleIndependentPhysBoneIndices);
                pat.physBoneEnableds.AddRange(_simpleIndependentPhysBoneIndices.Select(_ => true));
                cat.patterns.Add(pat);
                _categories.Add(cat);
            }

            if (_categories.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "メッシュが1つも選択されていません。", "OK");
                return;
            }

            ExecuteAnimationGeneration();
            if (_simpleOutfitMode != OutfitMode.CosB) GenerateAllSimpleIcons();
            if (_simpleOutfitMode != OutfitMode.CosB) ExecuteMenuFormatting();
            ExecuteLilToonSync();
            if (_simpleOutfitMode == OutfitMode.CosB) DisableAllRenderers();
            ApplyToPrefab();
            AutoSaveSettings();
        }

        void DisableAllRenderers()
        {
            if (_prefab == null) return;
            foreach (var r in _scannedRenderers)
            {
                if (r == null) continue;
                r.gameObject.SetActive(false);
                EditorUtility.SetDirty(r.gameObject);
            }
        }

        void RegenerateExistingIcons()
        {
            if (_simpleOutfitMode == OutfitMode.CosA || _simpleOutfitMode == OutfitMode.CosB)
            {
                var slots = _simpleOutfitMode == OutfitMode.CosA ? _simpleCosASlots : _simpleCosBSlots;
                for (int si = 0; si < slots.Length; si++)
                {
                    var slot = slots[si];
                    if (!slot.enabled) continue;
                    string key = IconKey(si, 0);
                    if (!_generatedIcons.ContainsKey(key) || _generatedIcons[key] == null) continue;
                    if (_existingIcons.ContainsKey(key) && _existingIcons[key] != null) continue;
                    int matchCi = _categories.FindIndex(c => c.parameterName == slot.parameterName);
                    if (matchCi >= 0)
                        GenerateIconForPatternWithKey(matchCi, 0, key);
                }
            }
            else
            {
                string key = IconKey(0, 0);
                if (_generatedIcons.ContainsKey(key) && _generatedIcons[key] != null
                    && !(_existingIcons.ContainsKey(key) && _existingIcons[key] != null)
                    && _categories.Count > 0)
                    GenerateIconForPatternWithKey(0, 0, key);
            }
        }

        void GenerateAllSimpleIcons()
        {
            if (_simpleOutfitMode == OutfitMode.CosA || _simpleOutfitMode == OutfitMode.CosB)
            {
                var slots = _simpleOutfitMode == OutfitMode.CosA ? _simpleCosASlots : _simpleCosBSlots;
                for (int si = 0; si < slots.Length; si++)
                {
                    var slot = slots[si];
                    if (!slot.enabled) continue;
                    bool autoBody = _simpleOutfitMode == OutfitMode.CosA
                                    && slot.parameterName == "CostumeBody";
                    if (!autoBody && slot.rendererIndices.Count == 0) continue;
                    string key = IconKey(si, 0);
                    if (_existingIcons.ContainsKey(key) && _existingIcons[key] != null) continue;
                    int matchCi = _categories.FindIndex(c => c.parameterName == slot.parameterName);
                    if (matchCi >= 0)
                        GenerateIconForPatternWithKey(matchCi, 0, key);
                }
            }
            else // Independent
            {
                if (_categories.Count > 0)
                {
                    string key = IconKey(0, 0);
                    if (!(_existingIcons.ContainsKey(key) && _existingIcons[key] != null))
                        GenerateIconForPatternWithKey(0, 0, key);
                }
            }
        }

        CategoryConfig BuildSimpleCategory(SimpleSlot slot, bool isCosB = false)
        {
            // SA_COS_A と共存する場合のみ value=3・COS_B連携が必要
            // SA_COS_A なし（置き換え）→ value=1、COS_B 不要
            bool needsCosB = _simpleOutfitMode == OutfitMode.CosA && _cosAPresent;

            var cat = new CategoryConfig
            {
                name = slot.label,
                parameterName = slot.parameterName,
                cosAParamIndex = Array.FindIndex(CosAStandardParams, p => p == slot.parameterName),
                parameterType = slot.paramType,
                blendMode = slot.blendMode,
                hasCosB = needsCosB,
                hideOnCastoff = slot.hideOnCastoff && !needsCosB,
                hideOnNaked   = slot.hideOnNaked   && !needsCosB
            };

            bool isReplacement = !needsCosB && _cosBPresent
                && slot.blendMode == BlendMode.Exclusive && slot.paramType == ParameterType.Int;

            // 置き換えモード+CostumeBody: CostumeBody=1でOFF、CostumeBody=2でNAKEDになるようflag強制
            if (isReplacement && slot.parameterName == "CostumeBody")
            {
                cat.hideOnCastoff = true;
                cat.hideOnNaked   = true;
            }

            if (slot.blendMode == BlendMode.Exclusive && slot.paramType == ParameterType.Int)
            {
                if (needsCosB)
                {
                    // SA_COS_A共存: COS_Bスロットは value=2
                    cat.patterns.Add(new PatternConfig { label = "COS_B", value = 2, isCosB = true });
                }
                else if (isReplacement && slot.parameterName == "CostumeBody")
                {
                    // FX は Greater(0) でまとめて処理。メニューは体切り替え(=1)とキャストオフ(=2)を両方出す
                    // value=1 はメニュー専用（FXステート不要）、value=2 がキャストオフ（メニュー末尾・DELETEアイコン）
                    cat.patterns.Add(new PatternConfig { label = "COS_B", value = 1, menuOnly = true });
                    cat.patterns.Add(new PatternConfig { label = "NAKED", value = 2, isCastoff = true });
                }
                else if (isReplacement)
                {
                    // Head など Body以外の置き換え: 衣装OFFトグル（value=1）をメニューに出す
                    cat.patterns.Add(new PatternConfig { label = "OFF", value = 1 });
                }
            }

            // SA_COS_A共存: value=3（SA制服=0,NAKED=1,COS_B=2 の後に配置）
            // COS_Bポジション: value=1（value=0=DEFAULT/OFF、value=1=ON）
            // SA_COS_A置き換え: value=0（改変者衣装がデフォルト衣装。COS_B/NAKED時に非表示）
            int startValue = needsCosB ? 3 : isCosB ? 1 : 0;
            var pat = new PatternConfig { label = "ON", value = startValue };

            bool autoSelectAll = slot.parameterName == "CostumeBody";
            if (autoSelectAll)
            {
                // CostumeBody置き換えモード: 他スロット（Head等）が選択済みのものを除いた全レンダラー・全PhysBoneを自動使用
                var otherRendererIdx = _simpleCosASlots
                    .Where(s => s != slot && s.enabled)
                    .SelectMany(s => s.rendererIndices)
                    .ToHashSet();
                var otherBoneIdx = _simpleCosASlots
                    .Where(s => s != slot && s.enabled)
                    .SelectMany(s => s.physBoneIndices)
                    .ToHashSet();

                var allIdx = Enumerable.Range(0, _scannedRenderers.Count)
                    .Where(i => !otherRendererIdx.Contains(i)).ToList();
                pat.rendererIndices.AddRange(allIdx);
                pat.rendererActives.AddRange(allIdx.Select(_ => true));
                var allBoneIdx = Enumerable.Range(0, _scannedPhysBones.Count)
                    .Where(i => !otherBoneIdx.Contains(i)).ToList();
                pat.physBoneIndices.AddRange(allBoneIdx);
                pat.physBoneEnableds.AddRange(allBoneIdx.Select(_ => true));
            }
            else
            {
                // Head・Skirt・共存モード: ユーザーが選択したレンダラーのみ
                pat.rendererIndices.AddRange(slot.rendererIndices);
                pat.rendererActives.AddRange(slot.rendererIndices.Select(_ => true));
                pat.physBoneIndices.AddRange(slot.physBoneIndices);
                pat.physBoneEnableds.AddRange(slot.physBoneIndices.Select(_ => true));
            }

            cat.patterns.Add(pat);
            return cat;
        }

        // ---------------- セクション1 ----------------

        void DrawPrefabSection()
        {
            EditorGUILayout.LabelField("1. プレハブ入力", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _prefab = (GameObject)EditorGUILayout.ObjectField("衣装プレハブ", _prefab, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                OnPrefabChanged();
            }

            if (_hasExistingMenu)
            {
                string existingMenuDesc = _existingMenuHandling switch {
                    ExistingMenuHandling.KeepAsIs        => "メニューには手を加えず、アニメーターと MA Parameters のみ更新します。他ツールや手動で組んだメニューを崩したくない場合に選択してください。",
                    ExistingMenuHandling.ConvertToFormat => "既存の MA Menu Installer を流用し、インストール先を 3+1 OutfitMenu に切り替えます。",
                    ExistingMenuHandling.Rebuild         => "メニューアセットを再生成して MA Menu Installer の参照を上書きします。このツールで何度も設定し直す場合はこちらを選択してください。",
                    _ => ""
                };
                EditorGUILayout.HelpBox("既存の MA メニュー設定が見つかりました。\n" + existingMenuDesc, MessageType.Info);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Toggle(_existingMenuHandling == ExistingMenuHandling.KeepAsIs,
                    new GUIContent("メニューはそのまま（アニメーターのみ更新）",
                        "他のツールや手動で組んだメニューを崩したくない場合に選択します。\nアニメーターと MA Parameters の更新のみ行い、メニューには一切手を加えません。"), "Button"))
                    _existingMenuHandling = ExistingMenuHandling.KeepAsIs;
                if (GUILayout.Toggle(_existingMenuHandling == ExistingMenuHandling.ConvertToFormat,
                    new GUIContent("3+1形式に付け替える",
                        "既存の MA Menu Installer を流用し、インストール先を 3+1 の OutfitMenu に切り替えます。\nメニューの中身（コントロール）はそのまま残ります。"), "Button"))
                    _existingMenuHandling = ExistingMenuHandling.ConvertToFormat;
                if (GUILayout.Toggle(_existingMenuHandling == ExistingMenuHandling.Rebuild,
                    new GUIContent("メニューを上書き再生成",
                        "このツールの設定をもとにメニューアセットを作り直し、MA Menu Installer の参照を上書きします。\n何度も設定し直す場合や初回セットアップに使用してください。"), "Button"))
                    _existingMenuHandling = ExistingMenuHandling.Rebuild;
                EditorGUILayout.EndHorizontal();
            }

        }

        void OnPrefabChanged()
        {
            _hasExistingMenu = false;
            _scannedRenderers.Clear();
            _scannedPhysBones.Clear();
            _iconLayers.Clear();
            _iconCameras.Clear();
            _previewTextures.Clear();
            _generatedIcons.Clear();
            _existingIcons.Clear();
            if (_prefab == null) return;

            foreach (var c in _prefab.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var fullName = c.GetType().FullName ?? "";
                if (fullName.Contains("ModularAvatarMenuInstaller") || fullName.Contains("ModularAvatarMenuItem"))
                {
                    _hasExistingMenu = true;
                    break;
                }
            }
            ScanPrefab();
            TryAutoLoadSettings();
            DetectCosA();
            DetectCosB();
            _previewDirty = true;
        }

        Transform GetAvatarRoot()
        {
            if (_prefab == null) return null;
            Transform t = _prefab.transform;
            while (t != null)
            {
                if (t.GetComponent("VRCAvatarDescriptor") != null
                    || t.GetComponents<Component>().Any(c =>
                        (c?.GetType().FullName ?? "").Contains("VRCAvatarDescriptor")))
                    return t;
                t = t.parent;
            }
            return _prefab.transform.root;
        }

        // アバタールート直下の子を走査し、名前に keyword を含む最初のオブジェクトを返す
        static Transform FindChildContaining(Transform root, string keyword)
        {
            for (int i = 0; i < root.childCount; i++)
            {
                var child = root.GetChild(i);
                if (child.name.IndexOf(keyword, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return child;
            }
            return null;
        }

        void DetectCosA()
        {
            bool prevDetected = _cosADetected;
            _cosADetected = false;

            if (_prefab == null || !_prefab.scene.IsValid())
            {
                if (prevDetected) _cosAPresent = false;
                return;
            }

            _cosADetected = FindChildContaining(GetAvatarRoot(), "COS_A") != null;
            if (_cosADetected && !prevDetected) _cosAPresent = true;
            if (!_cosADetected && prevDetected) _cosAPresent = false;
        }

        void DetectCosB()
        {
            bool prevDetected = _cosBDetected;
            _cosBDetected = false;

            if (_prefab == null || !_prefab.scene.IsValid())
            {
                if (prevDetected) _cosBPresent = false;
                return;
            }

            var found = FindChildContaining(GetAvatarRoot(), "COS_B");
            _cosBDetected = found != null;
            if (_cosBDetected) _cosBObjectName = found.name; // 検出名を保持（タオル非表示レイヤーで使用）
            if (_cosBDetected && !prevDetected) _cosBPresent = true;
            if (!_cosBDetected && prevDetected) _cosBPresent = false;
        }

        void ScanPrefab()
        {
            _scannedRenderers.Clear();
            _scannedPhysBones.Clear();
            if (_prefab == null) return;

            foreach (var r in _prefab.GetComponentsInChildren<Renderer>(true))
            {
                if (r is SkinnedMeshRenderer || r is MeshRenderer)
                    _scannedRenderers.Add(r);
            }
            foreach (var c in _prefab.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var fn = c.GetType().FullName ?? "";
                if (fn.Contains("VRCPhysBone") && !fn.Contains("Collider"))
                    _scannedPhysBones.Add(c);
            }
        }

        // ---------------- セクション2 ----------------

        void DrawModeSection()
        {
            EditorGUILayout.LabelField("2. モード選択", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(_mode == OutfitMode.CosA,        "COS_Aポジション", "Button")) _mode = OutfitMode.CosA;
            if (GUILayout.Toggle(_mode == OutfitMode.CosB,        "COS_Bポジション", "Button")) _mode = OutfitMode.CosB;
            if (GUILayout.Toggle(_mode == OutfitMode.Independent,  "独立",            "Button")) _mode = OutfitMode.Independent;
            EditorGUILayout.EndHorizontal();

            EditorGUI.indentLevel++;
            switch (_mode)
            {
                case OutfitMode.CosA:
                    EditorGUILayout.HelpBox("カテゴリごとにメニューコントロールを生成してOutfitMenuにインストールします。", MessageType.Info);
                    _cosAOutfitMenu = (VRCExpressionsMenu)EditorGUILayout.ObjectField(
                        new GUIContent("インストール先 OutfitMenu", "COS_Aメニューをインストールする VRCExpressionsMenu アセット"),
                        _cosAOutfitMenu, typeof(VRCExpressionsMenu), false);
                    DrawCosADetectionToggle();
                    break;
                case OutfitMode.CosB:
                    EditorGUILayout.HelpBox("メニュー生成なし。アニメーターとMA Parametersを生成します。", MessageType.Info);
                    break;
                case OutfitMode.Independent:
                    _independentParameterName = EditorGUILayout.TextField("パラメーター名", _independentParameterName);
                    _independentInstallTarget = (VRCExpressionsMenu)EditorGUILayout.ObjectField(
                        "インストール先", _independentInstallTarget, typeof(VRCExpressionsMenu), false);
                    break;
            }
            EditorGUI.indentLevel--;
        }

        void DrawCosADetectionToggle(bool showCosBRow = true)
        {
            if (_prefab != null && _prefab.scene.IsValid())
            {
                // COS_A 行
                EditorGUILayout.BeginHorizontal();
                var styleA = new GUIStyle(EditorStyles.miniLabel);
                styleA.normal.textColor = _cosADetected ? new Color(0.2f, 0.7f, 0.2f) : new Color(0.8f, 0.5f, 0.1f);
                EditorGUILayout.LabelField(_cosADetected ? "● COS_A 検出" : "○ COS_A 未検出", styleA, GUILayout.Width(120));
                bool newPresent = EditorGUILayout.ToggleLeft(
                    new GUIContent("COS_A ポジションがある", "SA_COS_A など COS_A 枠を持つDLCが入っている場合にオンにします。自動検出の結果と異なる場合のみ手動で変更してください。"),
                    _cosAPresent);
                if (newPresent != _cosAPresent)
                    _cosAPresent = newPresent;
                EditorGUILayout.EndHorizontal();

                // COS_B 行（COS_Bモードでは表示しない）
                if (showCosBRow)
                {
                    EditorGUILayout.BeginHorizontal();
                    var styleB = new GUIStyle(EditorStyles.miniLabel);
                    styleB.normal.textColor = _cosBDetected ? new Color(0.2f, 0.7f, 0.2f) : new Color(0.8f, 0.5f, 0.1f);
                    EditorGUILayout.LabelField(_cosBDetected ? "● COS_B 検出" : "○ COS_B 未検出", styleB, GUILayout.Width(120));
                    bool newCosBPresent = EditorGUILayout.ToggleLeft(
                        new GUIContent("COS_B ポジションがある", "SA_COS_B などユーザー作成の COS_B 枠がある場合にオンにします。自動検出の結果と異なる場合のみ手動で変更してください。"),
                        _cosBPresent);
                    if (newCosBPresent != _cosBPresent)
                        _cosBPresent = newCosBPresent;
                    EditorGUILayout.EndHorizontal();
                }
            }
            else
            {
                _cosAPresent = EditorGUILayout.ToggleLeft(
                    new GUIContent("COS_A ポジションがある", "SA_COS_A など COS_A 枠を持つDLCが入っている場合にオンにします。シーンに配置すると自動検出されます。"),
                    _cosAPresent);
                if (showCosBRow)
                    _cosBPresent = EditorGUILayout.ToggleLeft(
                        new GUIContent("COS_B ポジションがある", "SA_COS_B などユーザー作成の COS_B 枠がある場合にオンにします。シーンに配置すると自動検出されます。"),
                        _cosBPresent);
            }
        }

        // ---------------- セクション3 ----------------

        void DrawCategorySection()
        {
            EditorGUILayout.LabelField("3. カテゴリビルダー", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("メッシュ一覧を更新")) ScanPrefab();
            if (GUILayout.Button("COS_Aテンプレート"))
            {
                if (_categories.Count == 0 || EditorUtility.DisplayDialog(
                    "テンプレート読み込み", "現在のカテゴリをCOS_Aテンプレートで上書きしますか？", "上書き", "キャンセル"))
                {
                    _categories = new List<CategoryConfig>
                    {
                        new CategoryConfig { name = "Head",  cosAParamIndex = 0, parameterType = ParameterType.Int,  blendMode = BlendMode.Exclusive, hasCosB = false },
                        new CategoryConfig { name = "Body",  cosAParamIndex = 1, parameterType = ParameterType.Int,  blendMode = BlendMode.Exclusive, hasCosB = _cosAPresent },
                        new CategoryConfig { name = "Skirt", cosAParamIndex = 2, parameterType = ParameterType.Bool, blendMode = BlendMode.Combine,   hasCosB = false },
                    };
                }
            }
            EditorGUILayout.EndHorizontal();

            for (int ci = 0; ci < _categories.Count; ci++)
            {
                var cat = _categories[ci];
                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                cat.foldout = EditorGUILayout.Foldout(cat.foldout, "", true);
                cat.name = EditorGUILayout.TextField(cat.name);
                GUI.enabled = ci > 0;
                if (GUILayout.Button("▲", GUILayout.Width(24)))
                    (_categories[ci - 1], _categories[ci]) = (_categories[ci], _categories[ci - 1]);
                GUI.enabled = ci < _categories.Count - 1;
                if (GUILayout.Button("▼", GUILayout.Width(24)))
                    (_categories[ci + 1], _categories[ci]) = (_categories[ci], _categories[ci + 1]);
                GUI.enabled = true;
                if (GUILayout.Button("×", GUILayout.Width(24)))
                {
                    _categories.RemoveAt(ci);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                if (cat.foldout)
                {
                    EditorGUI.indentLevel++;
                    if (_mode == OutfitMode.CosA)
                    {
                        cat.cosAParamIndex = EditorGUILayout.Popup("パラメーター", cat.cosAParamIndex, CosAStandardParams);
                        if (cat.cosAParamIndex == CosAStandardParams.Length - 1)
                            cat.parameterName = EditorGUILayout.TextField("カスタム名", cat.parameterName);

                        if (cat.cosAParamIndex == 2) // CostumeSkirt
                        {
                            cat.parameterType = ParameterType.Bool;
                            cat.blendMode = BlendMode.Combine;
                        }
                        else if (cat.cosAParamIndex < 2) // CostumeHead / CostumeBody
                        {
                            cat.parameterType = ParameterType.Int;
                        }
                    }
                    else
                    {
                        cat.parameterName = EditorGUILayout.TextField("パラメーター名", cat.parameterName);
                    }
                    cat.parameterType = (ParameterType)EditorGUILayout.EnumPopup("型", cat.parameterType);
                    cat.blendMode = (BlendMode)EditorGUILayout.EnumPopup("モード", cat.blendMode);

                    // Exclusive(Int): value=0はSA制服デフォルト（DefaultState自動処理）
                    bool isExclusive = cat.blendMode == BlendMode.Exclusive && cat.parameterType == ParameterType.Int;
                    if (isExclusive)
                    {
                        // COS_Bスロット（value=2）の有無をトグル
                        bool newHasCosB = EditorGUILayout.ToggleLeft("COS_B ポジションがある（value=2をCOS_B用に確保）", cat.hasCosB);
                        if (newHasCosB != cat.hasCosB)
                        {
                            cat.hasCosB = newHasCosB;
                            if (!newHasCosB) cat.patterns.RemoveAll(p => p.isCosB);
                        }
                        if (cat.hasCosB && !cat.patterns.Any(p => p.isCosB))
                        {
                            int insertIdx = cat.patterns.FindIndex(p => !p.isCosB);
                            if (insertIdx < 0) insertIdx = cat.patterns.Count;
                            cat.patterns.Insert(insertIdx, new PatternConfig { label = "COS_B", value = 2, isCosB = true });
                        }
                    }

                    for (int pi = 0; pi < cat.patterns.Count; pi++)
                    {
                        var pat = cat.patterns[pi];
                        bool selected = _selectedCategoryForPattern == ci && _selectedPatternIndex == pi;

                        if (pat.isCosB)
                        {
                            // COS_B 固定スロット（value=2 固定・削除不可）
                            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                            {
                                GUILayout.Label("COS_B専用スロット", EditorStyles.miniLabel, GUILayout.Width(100));
                                pat.label = EditorGUILayout.TextField(pat.label);
                                GUILayout.Label("value=2 固定", EditorStyles.miniLabel, GUILayout.ExpandWidth(false));
                            }
                        }
                        else
                        {
                            EditorGUILayout.BeginHorizontal();
                            string toggleLabel = selected ? "▼ 閉じる" : "▶ 編集";
                            if (GUILayout.Button(toggleLabel, GUILayout.Width(60)))
                            {
                                if (selected) { _selectedCategoryForPattern = -1; _selectedPatternIndex = -1; }
                                else { _selectedCategoryForPattern = ci; _selectedPatternIndex = pi; }
                            }
                            pat.label = EditorGUILayout.TextField(pat.label);
                            if (cat.parameterType == ParameterType.Int)
                            {
                                GUILayout.Label("value:", EditorStyles.miniLabel, GUILayout.Width(36));
                                pat.value = Mathf.Max(1, EditorGUILayout.IntField(pat.value, GUILayout.Width(40)));
                            }
                            if (GUILayout.Button("削除", GUILayout.Width(36)))
                            {
                                cat.patterns.RemoveAt(pi);
                                EditorGUILayout.EndHorizontal();
                                break;
                            }
                            EditorGUILayout.EndHorizontal();
                        }

                        if (selected && pi < cat.patterns.Count)
                            DrawPatternChecklist(pat);
                    }

                    if (GUILayout.Button("+ パターン追加"))
                    {
                        int nextValue = cat.patterns.Where(p => !p.isCosB).Select(p => p.value).DefaultIfEmpty(0).Max() + 1;
                        cat.patterns.Add(new PatternConfig { value = nextValue });
                    }
                    EditorGUI.indentLevel--;
                }

                EditorGUILayout.EndVertical();
            }

            if (GUILayout.Button("+ カテゴリ追加"))
                _categories.Add(new CategoryConfig());
        }

        void DrawPatternChecklist(PatternConfig pat)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("このパターンで制御するオブジェクト", EditorStyles.miniBoldLabel);
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUI.indentLevel * 15);
            GUILayout.Label("対象", EditorStyles.miniLabel, GUILayout.Width(140));
            GUILayout.Label("このパターンでのON/OFF", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
            for (int ri = 0; ri < _scannedRenderers.Count; ri++)
            {
                var r = _scannedRenderers[ri];
                if (r == null) continue;
                int idx = pat.rendererIndices.IndexOf(ri);
                bool on = idx >= 0;
                bool active = on && pat.rendererActives[idx];

                EditorGUILayout.BeginHorizontal();
                string rcLabel = GetShortPath(r.transform);
                if (!r.gameObject.activeSelf) rcLabel += " (非表示)";
                var rc = new GUIContent(rcLabel, GetRelativePath(r.transform));
                bool newOn = EditorGUILayout.ToggleLeft(rc, on);
                if (newOn != on)
                {
                    if (newOn)
                    {
                        pat.rendererIndices.Add(ri);
                        pat.rendererActives.Add(true);
                        AutoSelectPhysBonesForPattern(pat, ri);
                    }
                    else
                    {
                        pat.rendererActives.RemoveAt(idx);
                        pat.rendererIndices.RemoveAt(idx);
                        RecalculatePhysBonesForPattern(pat);
                    }
                }
                if (on)
                {
                    bool newActive = GUILayout.Toggle(active, active ? "表示" : "非表示",
                        EditorStyles.miniButton, GUILayout.Width(48));
                    if (newActive != active) pat.rendererActives[idx] = newActive;
                }
                EditorGUILayout.EndHorizontal();
            }

            if (_scannedPhysBones.Count > 0)
            {
                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("このパターンで制御するPhysBone", EditorStyles.miniBoldLabel);
                EditorGUILayout.BeginHorizontal();
                GUILayout.Space(EditorGUI.indentLevel * 15);
                GUILayout.Label("対象", EditorStyles.miniLabel, GUILayout.Width(140));
                GUILayout.Label("このパターンでの有効/無効", EditorStyles.miniLabel);
                EditorGUILayout.EndHorizontal();
                for (int bi = 0; bi < _scannedPhysBones.Count; bi++)
                {
                    var pb = _scannedPhysBones[bi];
                    if (pb == null) continue;
                    int idx = pat.physBoneIndices.IndexOf(bi);
                    bool on = idx >= 0;
                    bool enabled = on && pat.physBoneEnableds[idx];

                    EditorGUILayout.BeginHorizontal();
                    var pbc = new GUIContent(GetShortPath(pb.transform), GetRelativePath(pb.transform));
                    bool newOn = EditorGUILayout.ToggleLeft(pbc, on);
                    if (newOn != on)
                    {
                        if (newOn) { pat.physBoneIndices.Add(bi); pat.physBoneEnableds.Add(true); }
                        else { pat.physBoneEnableds.RemoveAt(idx); pat.physBoneIndices.RemoveAt(idx); }
                    }
                    if (on)
                    {
                        bool newEnabled = GUILayout.Toggle(enabled, enabled ? "有効" : "無効",
                            EditorStyles.miniButton, GUILayout.Width(48));
                        if (newEnabled != enabled) pat.physBoneEnableds[idx] = newEnabled;
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }
            EditorGUI.indentLevel--;
        }

        string GetRelativePath(Transform t)
        {
            if (_prefab == null) return t.name;
            if (t == _prefab.transform) return "";
            var parts = new List<string>();
            while (t != null && t != _prefab.transform)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        // パスが深い場合は末尾2段だけ表示（ツールチップにフルパスを持たせる）
        string GetShortPath(Transform t)
        {
            var full = GetRelativePath(t);
            if (string.IsNullOrEmpty(full)) return "(root)";
            var parts = full.Split('/');
            return parts.Length <= 2 ? full : "…/" + parts[parts.Length - 2] + "/" + parts[parts.Length - 1];
        }

        // ---------------- セクション4 ----------------

        static string IconKey(int ci, int pi) => ci + "_" + pi;

        // key に対応するレンダラーインデックスを返す（プレビュー用）
        List<int> GetRendererIndicesForKey(string key)
        {
            var parts = key.Split('_');
            if (parts.Length < 2) return null;
            if (!int.TryParse(parts[0], out int ci) || !int.TryParse(parts[1], out int pi)) return null;

            // 簡単モード: スロット配列インデックスから直接引く（生成前・カテゴリ順ずれに対応）
            if (_simpleMode)
            {
                var slots = _simpleOutfitMode == OutfitMode.CosA ? _simpleCosASlots
                          : _simpleOutfitMode == OutfitMode.CosB ? _simpleCosBSlots
                          : null;
                if (slots != null && ci < slots.Length && slots[ci].rendererIndices.Count > 0)
                    return slots[ci].rendererIndices;
            }

            if (ci >= _categories.Count) return null;
            var cat = _categories[ci];
            // ON パターン（value=0）を優先、なければ pi 番目
            var onPat = cat.patterns.FirstOrDefault(p => p.value == 0 && !p.menuOnly);
            var pat = onPat ?? (pi < cat.patterns.Count ? cat.patterns[pi] : null);
            if (pat == null || pat.rendererIndices.Count == 0) return null;
            return pat.rendererIndices;
        }

        List<IconLayerConfig> GetIconLayers(string key)
        {
            if (!_iconLayers.TryGetValue(key, out var layers))
            {
                var defaultFrame = AssetDatabase.LoadAssetAtPath<Texture2D>(FrameIconPaths[0]);
                layers = new List<IconLayerConfig>
                {
                    new IconLayerConfig { type = IconLayerConfig.LayerType.Thumbnail },
                    new IconLayerConfig { type = IconLayerConfig.LayerType.Frame, texture = defaultFrame, visible = true },
                };
                _iconLayers[key] = layers;
            }
            return layers;
        }

        IconCameraConfig GetCameraConfig(string key)
        {
            if (!_iconCameras.TryGetValue(key, out var cfg))
            {
                cfg = new IconCameraConfig();
                _iconCameras[key] = cfg;
            }
            return cfg;
        }

        void DrawIconSection()
        {
            EditorGUILayout.LabelField(_simpleMode ? "アイコン設定" : "4. アイコン生成", EditorStyles.boldLabel);

            if (!_simpleMode && _categories.Count == 0)
            {
                EditorGUILayout.HelpBox("生成後にここでアイコンを設定して再生成できます。", MessageType.None);
                return;
            }

            if (_simpleMode)
            {
                // 簡単モード: 有効スロット単位でアイコン設定（生成前でも表示）
                // キーはスロット配列インデックス固定なので生成前後でずれない
                var slots = _simpleOutfitMode == OutfitMode.CosA ? _simpleCosASlots
                          : _simpleOutfitMode == OutfitMode.CosB ? _simpleCosBSlots
                          : null;
                if (slots == null || _prefab == null)
                {
                    EditorGUILayout.HelpBox("プレハブとモードを設定してください。", MessageType.None);
                    return;
                }

                for (int si = 0; si < slots.Length; si++)
                {
                    var slot = slots[si];
                    if (!slot.enabled) continue;
                    // Body（自動）はメッシュ選択不要なので表示する。Skirt等はメッシュが1つも選ばれていなければスキップ
                    bool autoBody = _simpleOutfitMode == OutfitMode.CosA
                                    && slot.parameterName == "CostumeBody";
                    if (!autoBody && slot.rendererIndices.Count == 0) continue;

                    string key = IconKey(si, 0);
                    if (!_iconFoldouts.ContainsKey(key)) _iconFoldouts[key] = false;
                    bool wasFoldoutOpen = _iconFoldouts[key];

                    EditorGUILayout.BeginVertical("box");
                    _iconFoldouts[key] = EditorGUILayout.Foldout(_iconFoldouts[key], slot.label, true);
                    if (_iconFoldouts[key] && !wasFoldoutOpen) _previewDirty = true; // 開いた瞬間にプレビュー生成
                    if (_iconFoldouts[key])
                    {
                        _existingIcons.TryGetValue(key, out var existingIcon);
                        var newExisting = (Texture2D)EditorGUILayout.ObjectField("既存アイコンを使用", existingIcon, typeof(Texture2D), false);
                        if (newExisting != existingIcon)
                        {
                            if (newExisting != null) _existingIcons[key] = newExisting;
                            else _existingIcons.Remove(key);
                        }

                        bool hasExisting = _existingIcons.ContainsKey(key) && _existingIcons[key] != null;
                        if (hasExisting)
                        {
                            EditorGUILayout.HelpBox("既存アイコンを使用します。生成は不要です。", MessageType.Info);
                        }
                        else
                        {
                            // プレビューをカメラ設定の上に配置（カメラ展開時に押し出されないよう）
                            if (_previewTextures.TryGetValue(key, out var preview) && preview != null)
                            {
                                EditorGUILayout.BeginHorizontal();
                                var rect = GUILayoutUtility.GetRect(IconSize, IconSize, GUILayout.Width(IconSize), GUILayout.Height(IconSize));
                                GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit, true);
                                EditorGUILayout.EndHorizontal();
                            }
                            else
                            {
                                // プレビュー未生成時はプレースホルダー
                                var placeholderStyle = new GUIStyle(EditorStyles.helpBox) { alignment = TextAnchor.MiddleCenter };
                                GUILayout.Box("プレビュー\n（設定を変更すると更新されます）",
                                    placeholderStyle, GUILayout.Width(IconSize), GUILayout.Height(IconSize));
                            }

                            EditorGUI.BeginChangeCheck();
                            DrawIconLayerStack(key);
                            DrawCameraPanel(key);
                            if (EditorGUI.EndChangeCheck()) _previewDirty = true;

                        }
                    }
                    EditorGUILayout.EndVertical();
                }

                bool hasGeneratedIcons = _generatedIcons.Values.Any(t => t != null);
                GUI.enabled = hasGeneratedIcons;
                if (GUILayout.Button(new GUIContent("アイコンを再生成", "すでに生成済みのアイコンのみ再レンダリングします。")))
                    RegenerateExistingIcons();
                GUI.enabled = true;
            }
            else
            {
                // 詳細モード: パターン単位
                for (int ci = 0; ci < _categories.Count; ci++)
                {
                    var cat = _categories[ci];
                    for (int pi = 0; pi < cat.patterns.Count; pi++)
                    {
                        var pat = cat.patterns[pi];
                        string key = IconKey(ci, pi);
                        if (!_iconFoldouts.ContainsKey(key)) _iconFoldouts[key] = false;
                        bool wasFoldoutOpenD = _iconFoldouts[key];

                        EditorGUILayout.BeginVertical("box");
                        _iconFoldouts[key] = EditorGUILayout.Foldout(_iconFoldouts[key], cat.name + " / " + pat.label, true);
                        if (_iconFoldouts[key] && !wasFoldoutOpenD) _previewDirty = true;
                        if (_iconFoldouts[key])
                        {
                            _existingIcons.TryGetValue(key, out var existingIcon);
                            var newExisting = (Texture2D)EditorGUILayout.ObjectField("既存アイコンを使用", existingIcon, typeof(Texture2D), false);
                            if (newExisting != existingIcon)
                            {
                                if (newExisting != null) _existingIcons[key] = newExisting;
                                else _existingIcons.Remove(key);
                            }

                            bool hasExisting = _existingIcons.ContainsKey(key) && _existingIcons[key] != null;
                            if (hasExisting)
                            {
                                EditorGUILayout.HelpBox("既存アイコンを使用します。生成は不要です。", MessageType.Info);
                            }
                            else
                            {
                                if (_previewTextures.TryGetValue(key, out var preview) && preview != null)
                                {
                                    EditorGUILayout.BeginHorizontal();
                                    var rect = GUILayoutUtility.GetRect(IconSize, IconSize, GUILayout.Width(IconSize), GUILayout.Height(IconSize));
                                    GUI.DrawTexture(rect, preview, ScaleMode.ScaleToFit, true);
                                    EditorGUILayout.EndHorizontal();
                                }
                                else
                                {
                                    var placeholderStyle = new GUIStyle(EditorStyles.helpBox) { alignment = TextAnchor.MiddleCenter };
                                    GUILayout.Box("プレビュー\n（設定を変更すると更新されます）",
                                        placeholderStyle, GUILayout.Width(IconSize), GUILayout.Height(IconSize));
                                }

                                EditorGUI.BeginChangeCheck();
                                DrawIconLayerStack(key);
                                DrawCameraPanel(key);
                                if (EditorGUI.EndChangeCheck()) _previewDirty = true;

                                if (GUILayout.Button("このパターンのアイコンを生成"))
                                    GenerateIconForPattern(ci, pi);
                            }
                        }
                        EditorGUILayout.EndVertical();
                    }
                }
            }
        }

        void DrawIconLayerStack(string key)
        {
            var layers = GetIconLayers(key);
            for (int li = 0; li < layers.Count; li++)
            {
                var layer = layers[li];
                EditorGUILayout.BeginHorizontal();
                layer.visible = EditorGUILayout.Toggle(layer.visible, GUILayout.Width(20));
                EditorGUILayout.LabelField(layer.type.ToString(), GUILayout.Width(70));
                if (layer.type == IconLayerConfig.LayerType.Frame)
                {
                    // FRAME1〜3 サムネイル選択
                    for (int fi = 0; fi < FrameIconPaths.Length; fi++)
                    {
                        var frameTex = AssetDatabase.LoadAssetAtPath<Texture2D>(FrameIconPaths[fi]);
                        if (frameTex == null) continue;
                        bool selected = layer.texture == frameTex;
                        var style = new GUIStyle(GUI.skin.button)
                        {
                            padding = new RectOffset(2, 2, 2, 2),
                            normal  = { background = selected ? Texture2D.whiteTexture : null },
                        };
                        if (GUILayout.Button(frameTex, style, GUILayout.Width(40), GUILayout.Height(40)))
                        {
                            layer.texture = selected ? null : frameTex; // 再クリックで解除
                            _previewDirty = true;
                        }
                    }
                    GUILayout.FlexibleSpace();
                }
                else if (layer.type == IconLayerConfig.LayerType.Thumbnail)
                    EditorGUILayout.LabelField("(リアルタイムプレビュー描画)");
                else
                    layer.texture = (Texture2D)EditorGUILayout.ObjectField(layer.texture, typeof(Texture2D), false);

                GUI.enabled = li > 1;
                if (GUILayout.Button("▲", GUILayout.Width(24)))
                { (layers[li - 1], layers[li]) = (layers[li], layers[li - 1]); _previewDirty = true; }
                GUI.enabled = li > 0 && li < layers.Count - 1;
                if (GUILayout.Button("▼", GUILayout.Width(24)))
                { (layers[li + 1], layers[li]) = (layers[li], layers[li + 1]); _previewDirty = true; }
                GUI.enabled = layer.type != IconLayerConfig.LayerType.Thumbnail;
                if (GUILayout.Button("×", GUILayout.Width(24)))
                {
                    layers.RemoveAt(li);
                    _previewDirty = true;
                    EditorGUILayout.EndHorizontal();
                    GUI.enabled = true;
                    break;
                }
                GUI.enabled = true;
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (!layers.Any(l => l.type == IconLayerConfig.LayerType.Frame))
            {
                if (GUILayout.Button("+ フレーム追加"))
                {
                    var defaultFrame = AssetDatabase.LoadAssetAtPath<Texture2D>(FrameIconPaths[0]);
                    layers.Add(new IconLayerConfig { type = IconLayerConfig.LayerType.Frame, texture = defaultFrame });
                    _previewDirty = true;
                }
            }
            if (GUILayout.Button("+ オーバーレイ追加"))
            { layers.Add(new IconLayerConfig { type = IconLayerConfig.LayerType.Overlay }); _previewDirty = true; }
            EditorGUILayout.EndHorizontal();
        }

        void DrawCameraPanel(string key)
        {
            var cam = GetCameraConfig(key);
            cam.foldout = EditorGUILayout.Foldout(cam.foldout, "カメラ設定", true);
            if (!cam.foldout) return;
            EditorGUI.indentLevel++;

            bool editing = _sceneEditKey == key;
            var btnLabel = editing ? "■ シーンビュー編集中（クリックで終了）" : "▶ シーンビューで位置・ズーム調整";
            var btnStyle = new GUIStyle(GUI.skin.button);
            if (editing) btnStyle.normal.textColor = new Color(1f, 0.6f, 0f);
            if (GUILayout.Button(btnLabel, btnStyle))
            {
                _sceneEditKey = editing ? null : key;
                if (_sceneEditKey != null) SceneView.lastActiveSceneView?.Focus();
            }

            EditorGUI.BeginChangeCheck();
            var xy = EditorGUILayout.Vector2Field("Position Offset (XY)", new Vector2(cam.positionOffset.x, cam.positionOffset.y));
            cam.positionOffset = new Vector3(xy.x, xy.y, cam.positionOffset.z);
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel("向き");
            if (GUILayout.Button("正面")) { cam.rotationEuler = new Vector3(0f, 180f, 0f); GUI.changed = true; }
            if (GUILayout.Button("背面")) { cam.rotationEuler = new Vector3(0f, 0f, 0f);   GUI.changed = true; }
            EditorGUILayout.EndHorizontal();
            cam.orthographicSize = EditorGUILayout.FloatField(
                new GUIContent("Zoom", "値が小さいほど拡大。デフォルト 0.02"), cam.orthographicSize);
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(this, "アイコンカメラ設定変更");
                SyncCamerasToSerializedList();
                _previewDirty = true;
            }

            if (GUILayout.Button("リセット"))
            {
                Undo.RecordObject(this, "アイコンカメラ設定リセット");
                SyncCamerasToSerializedList();
                cam.positionOffset   = Vector3.zero;
                cam.rotationEuler    = new Vector3(0f, 180f, 0f);
                cam.orthographicSize = 0.02f;
                SyncCamerasToSerializedList();
                _previewDirty = true;
            }
            EditorGUI.indentLevel--;
        }

        void RegenerateAllPreviews()
        {
            if (_prefab == null) return;
            foreach (var kv in _iconFoldouts.Where(kv => kv.Value).ToList())
            {
                var tex = RenderIcon(kv.Key);
                if (tex != null) _previewTextures[kv.Key] = tex;
            }
        }

        Texture2D RenderIcon(string key)
        {
            if (_prefab == null) return null;

            var camCfg = GetCameraConfig(key);
            var layers = GetIconLayers(key);

            // bounds計算（スロット指定レンダラー、なければ衣装全体）
            var renderers = _prefab.GetComponentsInChildren<Renderer>(true);
            var targetIndices = GetRendererIndicesForKey(key);
            var boundsRenderers = (targetIndices != null && targetIndices.Count > 0)
                ? targetIndices.Where(i => i < _scannedRenderers.Count && _scannedRenderers[i] != null)
                               .Select(i => _scannedRenderers[i]).ToList()
                : renderers.ToList();

            var bounds = new Bounds(_prefab.transform.position, Vector3.one * 0.1f);
            bool first = true;
            foreach (var r in boundsRenderers)
            {
                if (r == null) continue;
                if (first) { bounds = r.bounds; first = false; }
                else bounds.Encapsulate(r.bounds);
            }

            GameObject camObj = null;
            RenderTexture rt = null;
            var prevActive = RenderTexture.active;
            try
            {
                camObj = new GameObject("IconPreviewCamera");
                camObj.hideFlags = HideFlags.HideAndDontSave;
                var cam = camObj.AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.2f
                    * Mathf.Max(camCfg.orthographicSize / 0.1f, 0.01f);
                cam.cullingMask = ~0;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = new Color(0, 0, 0, 0);
                cam.nearClipPlane = 0.01f;
                cam.farClipPlane = 100f;
                cam.transform.rotation = Quaternion.Euler(camCfg.rotationEuler);
                // positionOffset.z はハンドル安定用の蓄積値のためXYのみ使用
                var xyOffset = new Vector3(camCfg.positionOffset.x, camCfg.positionOffset.y, 0f);
                cam.transform.position = bounds.center
                    - cam.transform.forward * (bounds.extents.magnitude + 1f)
                    + xyOffset;

                // 既存のディレクショナルライトを一時的に無効化
                var existingLights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsSortMode.None);
                var disabledLights = new List<Light>();
                foreach (var l in existingLights)
                {
                    if (l.type == LightType.Directional && l.enabled)
                    {
                        l.enabled = false;
                        disabledLights.Add(l);
                    }
                }

                // カメラにアタッチしたディレクショナルライトでアバターを照らす
                var lightObj = new GameObject("IconDirLight");
                lightObj.hideFlags = HideFlags.HideAndDontSave;
                lightObj.transform.SetParent(camObj.transform, false);
                var dirLight = lightObj.AddComponent<Light>();
                dirLight.type = LightType.Directional;
                dirLight.intensity = 1f;
                dirLight.color = Color.white;
                lightObj.transform.LookAt(bounds.center);

                rt = RenderTexture.GetTemporary(IconSize, IconSize, 24, RenderTextureFormat.ARGB32);
                cam.targetTexture = rt;
                cam.Render();

                // ライト後処理
                DestroyImmediate(lightObj);
                foreach (var l in disabledLights) l.enabled = true;

                RenderTexture.active = rt;
                var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                tex.Apply();

                // マスク適用（レイヤー合成前に適用してフレームはマスク外に出す）
                var maskPath = ResourceFolder + "/ICON_MASK.png";
                var maskAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(maskPath);
                if (maskAsset == null)
                    Debug.LogWarning("[OutfitFormatter] マスク画像が見つかりません: " + maskPath);
                else if (!maskAsset.isReadable)
                    Debug.LogWarning("[OutfitFormatter] マスク画像がReadableではありません: " + maskPath);
                else
                    ApplyMask(tex, maskAsset);

                foreach (var layer in layers)
                {
                    if (layer.type == IconLayerConfig.LayerType.Thumbnail) continue;
                    if (!layer.visible || layer.texture == null) continue;
                    CompositeLayer(tex, layer.texture);
                }

                return tex;
            }
            catch (Exception e)
            {
                Debug.LogError("[OutfitFormatter] アイコンレンダリングに失敗しました: " + e);
                return null;
            }
            finally
            {
                RenderTexture.active = prevActive;
                if (rt != null) RenderTexture.ReleaseTemporary(rt);
                if (camObj != null) DestroyImmediate(camObj);
            }
        }

        static void ApplyMask(Texture2D baseTex, Texture2D maskTex)
        {
            var resized = ResizeToIconSize(maskTex);
            Color[] pixels = baseTex.GetPixels();
            Color[] maskPixels = resized.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                float mask = maskPixels[i].grayscale;
                pixels[i].a *= mask;
            }
            baseTex.SetPixels(pixels);
            baseTex.Apply();
        }

        static void CompositeLayer(Texture2D baseTex, Texture2D layerTex)
        {
            var resized = ResizeToIconSize(layerTex);
            Color[] basePixels = baseTex.GetPixels();
            Color[] layerPixels = resized.GetPixels();
            for (int i = 0; i < basePixels.Length; i++)
            {
                float a = layerPixels[i].a;
                basePixels[i] = new Color(
                    a * layerPixels[i].r + (1 - a) * basePixels[i].r,
                    a * layerPixels[i].g + (1 - a) * basePixels[i].g,
                    a * layerPixels[i].b + (1 - a) * basePixels[i].b,
                    a + basePixels[i].a * (1 - a));
            }
            baseTex.SetPixels(basePixels);
            baseTex.Apply();
            if (resized != layerTex) DestroyImmediate(resized);
        }

        static Texture2D ResizeToIconSize(Texture2D src)
        {
            var rt = RenderTexture.GetTemporary(IconSize, IconSize, 0, RenderTextureFormat.ARGB32);
            var prev = RenderTexture.active;
            try
            {
                Graphics.Blit(src, rt);
                RenderTexture.active = rt;
                var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, IconSize, IconSize), 0, 0);
                tex.Apply();
                return tex;
            }
            finally
            {
                RenderTexture.active = prev;
                RenderTexture.ReleaseTemporary(rt);
            }
        }

        void GenerateIconForPatternWithKey(int ci, int pi, string key)
        {
            try
            {
                if (_prefab == null)
                {
                    EditorUtility.DisplayDialog("エラー", "衣装プレハブを設定してください。", "OK");
                    return;
                }
                var tex = RenderIcon(key);
                if (tex == null)
                {
                    EditorUtility.DisplayDialog("エラー", "アイコンのレンダリングに失敗しました。", "OK");
                    return;
                }
                EnsureOutputFolder();
                string safeName = _categories[ci].name + "_" + _categories[ci].patterns[pi].label;
                foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
                string path = _outputFolder + "/Icon_" + safeName + ".png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(path);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Default;
                    importer.alphaIsTransparency = true;
                    importer.SaveAndReimport();
                }
                var loaded = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                _generatedIcons[key] = loaded;
                // カテゴリキーでも登録してBuildControlsFromCategoriesから参照できるようにする
                _generatedIcons[IconKey(ci, pi)] = loaded;
                _previewTextures[key] = tex;
                SyncIconsToSerializedList();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("エラー", "アイコン生成中にエラーが発生しました:\n" + e.Message, "OK");
                Debug.LogException(e);
            }
        }

        void GenerateIconForPattern(int ci, int pi)
        {
            try
            {
                if (_prefab == null)
                {
                    EditorUtility.DisplayDialog("エラー", "衣装プレハブを設定してください。", "OK");
                    return;
                }
                string key = IconKey(ci, pi);
                var tex = RenderIcon(key);
                if (tex == null)
                {
                    EditorUtility.DisplayDialog("エラー", "アイコンのレンダリングに失敗しました。", "OK");
                    return;
                }
                EnsureOutputFolder();
                string safeName = _categories[ci].name + "_" + _categories[ci].patterns[pi].label;
                foreach (var c in Path.GetInvalidFileNameChars()) safeName = safeName.Replace(c, '_');
                string path = _outputFolder + "/Icon_" + safeName + ".png";
                File.WriteAllBytes(path, tex.EncodeToPNG());
                AssetDatabase.ImportAsset(path);
                var importer = AssetImporter.GetAtPath(path) as TextureImporter;
                if (importer != null)
                {
                    importer.textureType = TextureImporterType.Default;
                    importer.alphaIsTransparency = true;
                    importer.SaveAndReimport();
                }
                _generatedIcons[key] = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                _previewTextures[key] = tex;
                SyncIconsToSerializedList();
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("エラー", "アイコン生成中にエラーが発生しました:\n" + e.Message, "OK");
                Debug.LogException(e);
            }
        }

        // ---------------- セクション5 ----------------

        void DrawExecuteSection()
        {
            EditorGUILayout.LabelField("5. 実行", EditorStyles.boldLabel);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("適用方式", GUILayout.Width(70));
            if (GUILayout.Toggle(!_applyToPrefab, "フォルダに出力", "Button")) _applyToPrefab = false;
            if (GUILayout.Toggle(_applyToPrefab, "Prefabに直接適用", "Button")) _applyToPrefab = true;
            EditorGUILayout.EndHorizontal();
            if (!_applyToPrefab)
            {
                EditorGUILayout.BeginHorizontal();
                _outputFolder = EditorGUILayout.TextField("出力先", _outputFolder);
                if (GUILayout.Button("選択", GUILayout.Width(60)))
                {
                    var selected = EditorUtility.OpenFolderPanel("出力先フォルダを選択", "Assets", "");
                    if (!string.IsNullOrEmpty(selected) && selected.StartsWith(Application.dataPath))
                        _outputFolder = "Assets" + selected.Substring(Application.dataPath.Length);
                }
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                string preview = ResolveOutputFolder();
                EditorGUILayout.LabelField("出力先: " + preview, EditorStyles.miniLabel);
            }
            EditorGUILayout.Space(4);

            if (GUILayout.Button("アニメーション生成")) ExecuteAnimationGeneration();

            GUI.enabled = _mode != OutfitMode.CosB;
            if (GUILayout.Button("メニュー整形")) ExecuteMenuFormatting();
            GUI.enabled = true;

            if (GUILayout.Button("lilToon明るさ同期のみ実行")) ExecuteLilToonSync();

            if (GUILayout.Button("すべて実行", GUILayout.Height(36)))
            {
                ExecuteAnimationGeneration();
                if (_mode != OutfitMode.CosB) ExecuteMenuFormatting();
                ExecuteLilToonSync();
                if (_applyToPrefab) ApplyToPrefab();
            }
        }

        string ResolveOutputFolder()
        {
            if (_applyToPrefab && _prefab != null)
            {
                string prefabPath = AssetDatabase.GetAssetPath(_prefab);
                if (string.IsNullOrEmpty(prefabPath))
                {
                    // Hierarchyにある場合はPrefabAssetのパスを取得
                    prefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(_prefab);
                }
                if (!string.IsNullOrEmpty(prefabPath))
                    return Path.GetDirectoryName(prefabPath).Replace("\\", "/") + "/" + _prefab.name + "_generated";
            }
            return _outputFolder;
        }

        void EnsureOutputFolder()
        {
            var folder = ResolveOutputFolder();
            if (!AssetDatabase.IsValidFolder(folder))
            {
                var parts = folder.Split('/');
                string current = parts[0];
                for (int i = 1; i < parts.Length; i++)
                {
                    string next = current + "/" + parts[i];
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(current, parts[i]);
                    current = next;
                }
            }
            _outputFolder = folder;
        }

        // ---------------- アニメーション生成 ----------------

        void ExecuteAnimationGeneration()
        {
            try
            {
                if (_prefab == null)
                {
                    EditorUtility.DisplayDialog("エラー", "衣装プレハブを設定してください。", "OK");
                    return;
                }
                if (_scannedRenderers.Count == 0) ScanPrefab();
                EnsureOutputFolder();

                string outfitName = _prefab.name;
                string controllerPath = _outputFolder + "/" + outfitName + "_FX.controller";
                var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);

                // Base Layerを除去してから独自レイヤーを追加
                controller.layers = controller.layers.Where(l => l.name != "Base Layer").ToArray();

                foreach (var cat in _categories)
                {
                    if (string.IsNullOrEmpty(cat.ResolvedParameterName)) continue;
                    if (cat.blendMode == BlendMode.Exclusive && cat.parameterType == ParameterType.Int)
                        BuildExclusiveLayer(controller, cat);
                    else
                        BuildCombineLayer(controller, cat);
                }

                BuildBrightnessLayers(controller, outfitName);

                // SA_COS_A共存モード: 既存アニメーターにCostumeBody新値のOFF遷移を追加
                if (_simpleOutfitMode == OutfitMode.CosA && _cosAPresent)
                {
                    var bodyCat = _categories.FirstOrDefault(c => c.parameterName == "CostumeBody");
                    var onPat = bodyCat?.patterns.FirstOrDefault(p => p.label == "ON");
                    if (onPat != null)
                        PatchExistingCosBodyLayers(onPat.value);
                }

                _lastGeneratedController = controller;
                EditorUtility.SetDirty(controller);

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                if (!_applyToPrefab)
                    EditorUtility.DisplayDialog("完了", "AnimatorControllerを生成しました。\n" + controllerPath, "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("エラー", "アニメーション生成中にエラーが発生しました:\n" + e.Message, "OK");
                Debug.LogException(e);
            }
        }

        static void SetConstantCurve(AnimationClip clip, EditorCurveBinding binding, float value)
        {
            var kf = new Keyframe(0f, value)
            {
                inTangent = float.PositiveInfinity,
                outTangent = float.PositiveInfinity
            };
            var curve = new AnimationCurve(kf);
            AnimationUtility.SetKeyLeftTangentMode(curve, 0, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetKeyRightTangentMode(curve, 0, AnimationUtility.TangentMode.Constant);
            AnimationUtility.SetEditorCurve(clip, binding, curve);
        }

        void AddRendererKey(AnimationClip clip, Renderer r, bool active)
        {
            var binding = EditorCurveBinding.FloatCurve(GetRelativePath(r.transform), typeof(GameObject), "m_IsActive");
            SetConstantCurve(clip, binding, active ? 1f : 0f);
        }

        void AddPhysBoneKey(AnimationClip clip, Component pb, bool enabled)
        {
            var binding = new EditorCurveBinding
            {
                path = GetRelativePath(pb.transform),
                type = pb.GetType(),
                propertyName = "m_Enabled"
            };
            SetConstantCurve(clip, binding, enabled ? 1f : 0f);
        }

        // クリップをメモリ上で作成（まだ保存しない）
        static AnimationClip NewClip(string name) => new AnimationClip { name = name };

        // キー追加済みのクリップをアセットとして保存
        void SaveClipAsset(AnimationClip clip, string name)
        {
            string path = _outputFolder + "/" + name + ".anim";
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(path) != null)
                AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
        }

        // 後方互換（旧コードで使用中の箇所は順次 NewClip+SaveClipAsset に置き換え）
        AnimationClip CreateClipAsset(string name)
        {
            var clip = NewClip(name);
            SaveClipAsset(clip, name);
            return clip;
        }

        AnimatorControllerLayer AddLayer(AnimatorController controller, string layerName)
        {
            var sm = new AnimatorStateMachine { name = layerName, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(sm, controller);
            var layer = new AnimatorControllerLayer
            {
                name = layerName,
                defaultWeight = 1f,
                blendingMode = AnimatorLayerBlendingMode.Override,
                stateMachine = sm
            };
            var layers = controller.layers;
            var newLayers = new AnimatorControllerLayer[layers.Length + 1];
            Array.Copy(layers, newLayers, layers.Length);
            newLayers[layers.Length] = layer;
            controller.layers = newLayers;
            return layer;
        }

        void AddParameterIfMissing(AnimatorController controller, string name, AnimatorControllerParameterType type)
        {
            if (!controller.parameters.Any(p => p.name == name))
                controller.AddParameter(name, type);
        }

        void BuildExclusiveLayer(AnimatorController controller, CategoryConfig cat)
        {
            AddParameterIfMissing(controller, cat.ResolvedParameterName, AnimatorControllerParameterType.Int);
            if (cat.hideOnCastoff || cat.hideOnNaked)
                AddParameterIfMissing(controller, "CostumeBody", AnimatorControllerParameterType.Int);
            var layer = AddLayer(controller, "FX_" + cat.name);
            var sm = layer.stateMachine;
            string param = cat.ResolvedParameterName;

            var allRendererIdx = cat.patterns.SelectMany(p => p.rendererIndices).Distinct().ToList();
            var allPhysBoneIdx = cat.patterns.SelectMany(p => p.physBoneIndices).Distinct().ToList();

            sm.entryPosition    = new Vector3(GCX, GY0, 0);
            sm.anyStatePosition = new Vector3(GCX, GY0 + 40f, 0);
            sm.exitPosition     = new Vector3(GCX, GY0 - 40f, 0);

            var nonNakedPatterns = cat.patterns.Where(p => !p.isNaked && !p.menuOnly).OrderByDescending(p => p.value).ToList();
            int maxValue = nonNakedPatterns.Count > 0 ? nonNakedPatterns[0].value : 0;
            bool hasZeroPattern = nonNakedPatterns.Any(p => p.value == 0);

            // 全OFF クリップ（キー追加後に保存する）
            AnimationClip sharedOffClip = null;
            AnimationClip GetOrCreateOffClip()
            {
                if (sharedOffClip != null) return sharedOffClip;
                // キーを全て追加してからアセット保存（先保存すると curves が反映されない）
                var offClip = NewClip(cat.name + "_OFF");
                foreach (var ri in allRendererIdx)
                {
                    if (ri >= _scannedRenderers.Count || _scannedRenderers[ri] == null) continue;
                    AddRendererKey(offClip, _scannedRenderers[ri], false);
                }
                foreach (var bi in allPhysBoneIdx)
                {
                    if (bi >= _scannedPhysBones.Count || _scannedPhysBones[bi] == null) continue;
                    AddPhysBoneKey(offClip, _scannedPhysBones[bi], false);
                }
                SaveClipAsset(offClip, cat.name + "_OFF");
                sharedOffClip = offClip;
                return sharedOffClip;
            }

            // 共存モード（value=0 パターンなし）のみ DEFAULT ステートを生成
            AnimatorState defaultState = null;
            if (!hasZeroPattern)
            {
                defaultState = sm.AddState("DEFAULT", new Vector3(GX, GY0, 0));
                defaultState.motion = GetOrCreateOffClip();
                defaultState.writeDefaultValues = false;
                sm.defaultState = defaultState;
            }

            AnimatorState zeroPatternState = null;

            foreach (var pat in nonNakedPatterns)
            {
                bool isAllOff = pat.rendererIndices.Count == 0 && pat.physBoneIndices.Count == 0;
                AnimationClip clip;
                if (isAllOff)
                {
                    clip = GetOrCreateOffClip();
                }
                else
                {
                    // キーを全て追加してからアセット保存
                    clip = NewClip(cat.name + "_" + pat.label);
                    foreach (var ri in allRendererIdx)
                    {
                        if (ri >= _scannedRenderers.Count || _scannedRenderers[ri] == null) continue;
                        int idx = pat.rendererIndices.IndexOf(ri);
                        AddRendererKey(clip, _scannedRenderers[ri], idx >= 0 && pat.rendererActives[idx]);
                    }
                    foreach (var bi in allPhysBoneIdx)
                    {
                        if (bi >= _scannedPhysBones.Count || _scannedPhysBones[bi] == null) continue;
                        int idx = pat.physBoneIndices.IndexOf(bi);
                        AddPhysBoneKey(clip, _scannedPhysBones[bi], idx >= 0 && pat.physBoneEnableds[idx]);
                    }
                    SaveClipAsset(clip, cat.name + "_" + pat.label);
                }
                float stateY = hasZeroPattern ? GY0 + GYS * pat.value : GY0 + GYS * (1 + pat.value);
                var state = sm.AddState(pat.label, new Vector3(GX, stateY, 0));
                state.motion = clip;
                state.writeDefaultValues = false;

                var t = sm.AddAnyStateTransition(state);
                t.hasExitTime = false;
                t.duration = 0f;
                t.canTransitionToSelf = false;

                if (pat.value == 0)
                {
                    bool needsBodyCheck = cat.hideOnCastoff || cat.hideOnNaked;
                    if (needsBodyCheck)
                    {
                        // ON になる CostumeBody の範囲を決定
                        // hideOnCastoff=true: Body=1 は非表示
                        // hideOnNaked=true:   Body=2 は非表示
                        // 両方true: Body=0 のみ ON
                        int bodyMax = cat.hideOnCastoff ? 1 : 2; // Less(bodyMax) で上限
                        t.AddCondition(AnimatorConditionMode.Less, 1, param);
                        t.AddCondition(AnimatorConditionMode.Less, bodyMax, "CostumeBody");

                        if (!cat.hideOnNaked)
                        {
                            // hideOnCastoffのみ: Body=2（キャストオフ）は ON
                            var t2 = sm.AddAnyStateTransition(state);
                            t2.hasExitTime = false; t2.duration = 0f; t2.canTransitionToSelf = false;
                            t2.AddCondition(AnimatorConditionMode.Less, 1, param);
                            t2.AddCondition(AnimatorConditionMode.Greater, 1, "CostumeBody");
                        }
                    }
                    else
                    {
                        t.AddCondition(AnimatorConditionMode.Less, 1, param);
                    }
                    zeroPatternState = state;
                }
                else
                {
                    t.AddCondition(AnimatorConditionMode.Equals, pat.value, param);
                    // SA_COS_A共存でHead/Skirt等: BODYも同じvalue=3のときのみON
                    if (cat.hasCosB && param != "CostumeBody")
                        t.AddCondition(AnimatorConditionMode.Equals, pat.value, "CostumeBody");
                }
            }

            // COS_B_HIDE: CostumeBody=1（COS_B/温泉タオル）時に非表示
            if (cat.hideOnCastoff)
            {
                float cosBY = GY0 - GYS * 2;
                var cosBHideState = sm.AddState("COS_B_HIDE", new Vector3(GX, cosBY, 0));
                cosBHideState.motion = GetOrCreateOffClip();
                cosBHideState.writeDefaultValues = false;
                var tCosB = sm.AddAnyStateTransition(cosBHideState);
                tCosB.hasExitTime = false;
                tCosB.duration = 0f;
                tCosB.canTransitionToSelf = false;
                tCosB.AddCondition(AnimatorConditionMode.Equals, 1, "CostumeBody");
            }

            // NAKED_HIDE: CostumeBody=2（キャストオフ）時に非表示
            if (cat.hideOnNaked)
            {
                float nakedY = GY0 - GYS * 3;
                var nakedHideState = sm.AddState("NAKED_HIDE", new Vector3(GX, nakedY, 0));
                nakedHideState.motion = GetOrCreateOffClip();
                nakedHideState.writeDefaultValues = false;
                var tNaked = sm.AddAnyStateTransition(nakedHideState);
                tNaked.hasExitTime = false;
                tNaked.duration = 0f;
                tNaked.canTransitionToSelf = false;
                tNaked.AddCondition(AnimatorConditionMode.Greater, 1, "CostumeBody");
            }

            if (zeroPatternState != null)
                sm.defaultState = zeroPatternState;

            // 共存モードのみ: AnyState → DEFAULT（いずれのパターン値でもない場合）
            if (defaultState != null)
            {
                var toDefault = sm.AddAnyStateTransition(defaultState);
                toDefault.hasExitTime = false;
                toDefault.duration = 0f;
                toDefault.canTransitionToSelf = false;
                foreach (var p in nonNakedPatterns)
                    toDefault.AddCondition(AnimatorConditionMode.NotEqual, p.value, param);

                // SA_COS_A共存でHead/Skirt等: BODYがOFF(!=maxValue)のときもDEFAULTへ
                if (cat.hasCosB && param != "CostumeBody")
                {
                    var toDefaultBody = sm.AddAnyStateTransition(defaultState);
                    toDefaultBody.hasExitTime = false;
                    toDefaultBody.duration = 0f;
                    toDefaultBody.canTransitionToSelf = false;
                    toDefaultBody.AddCondition(AnimatorConditionMode.NotEqual, maxValue, "CostumeBody");
                }
            }
        }

        // SA_COS_A など既存アニメーターの CostumeBody レイヤーに新しい値の OFF 遷移を追加
        void PatchExistingCosBodyLayers(int newCosBodyValue)
        {
            var avatarRoot = GetAvatarRoot();
            if (avatarRoot == null) return;

            var mergeAnimType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => (t.FullName ?? "").Contains("ModularAvatarMergeAnimator")
                    && typeof(Component).IsAssignableFrom(t));
            if (mergeAnimType == null) return;

            foreach (Component comp in avatarRoot.GetComponentsInChildren(mergeAnimType, true))
            {
                // 今回生成した prefab 配下はスキップ
                if (_prefab != null && comp.transform.IsChildOf(_prefab.transform)) continue;

                var so = new SerializedObject(comp);
                var controller = so.FindProperty("animator")?.objectReferenceValue as AnimatorController;
                if (controller == null) continue;

                bool patched = false;
                foreach (var layer in controller.layers)
                    patched |= PatchStateMachineForCosBody(layer.stateMachine, newCosBodyValue);

                if (patched)
                    EditorUtility.SetDirty(controller);
            }
            AssetDatabase.SaveAssets();
        }

        bool PatchStateMachineForCosBody(AnimatorStateMachine sm, int newValue)
        {
            // CostumeBody を使う AnyState 遷移がないレイヤーはスキップ
            if (!sm.anyStateTransitions.Any(t => t.conditions.Any(c => c.parameter == "CostumeBody")))
                return false;

            // 既に newValue を処理していればスキップ
            if (sm.anyStateTransitions.Any(t => t.conditions.Any(c =>
                    c.parameter == "CostumeBody"
                    && c.mode == AnimatorConditionMode.Equals
                    && Mathf.Approximately(c.threshold, newValue))))
                return false;

            // 遷移先ステートの候補: CostumeBody != 0 の既存遷移先 → NAKED/OFF 系の名前 → 最初のステート
            AnimatorState target = null;
            foreach (var t in sm.anyStateTransitions)
            {
                var cond = t.conditions.FirstOrDefault(c => c.parameter == "CostumeBody");
                if (cond.parameter == null) continue;
                bool isOffValue = cond.mode == AnimatorConditionMode.Greater && Mathf.Approximately(cond.threshold, 0)
                    || cond.mode == AnimatorConditionMode.Equals && !Mathf.Approximately(cond.threshold, 0);
                if (isOffValue && t.destinationState != null) { target = t.destinationState; break; }
            }
            if (target == null)
            {
                target = sm.states
                    .Select(s => s.state)
                    .FirstOrDefault(s => { var n = s.name.ToLower(); return n.Contains("naked") || n.Contains("default") || n.Contains("off"); });
            }
            if (target == null) return false;

            var newT = sm.AddAnyStateTransition(target);
            newT.hasExitTime = false;
            newT.duration = 0f;
            newT.canTransitionToSelf = false;
            newT.AddCondition(AnimatorConditionMode.Equals, newValue, "CostumeBody");
            return true;
        }

        void BuildCombineLayer(AnimatorController controller, CategoryConfig cat)
        {
            AddParameterIfMissing(controller, cat.ResolvedParameterName, AnimatorControllerParameterType.Bool);
            if (cat.hideOnCastoff || cat.hideOnNaked)
                AddParameterIfMissing(controller, "CostumeBody", AnimatorControllerParameterType.Int);
            var layer = AddLayer(controller, "FX_" + cat.name);
            var sm = layer.stateMachine;

            var pat = cat.patterns.FirstOrDefault() ?? new PatternConfig();

            var offClip = NewClip(cat.name + "_OFF");
            var onClip = NewClip(cat.name + "_ON");

            foreach (var ri in pat.rendererIndices)
            {
                if (ri >= _scannedRenderers.Count || _scannedRenderers[ri] == null) continue;
                int idx = pat.rendererIndices.IndexOf(ri);
                bool active = idx >= 0 && pat.rendererActives[idx];
                AddRendererKey(offClip, _scannedRenderers[ri], false);
                AddRendererKey(onClip, _scannedRenderers[ri], active);
            }
            foreach (var bi in pat.physBoneIndices)
            {
                if (bi >= _scannedPhysBones.Count || _scannedPhysBones[bi] == null) continue;
                int idx = pat.physBoneIndices.IndexOf(bi);
                bool enabled = idx >= 0 && pat.physBoneEnableds[idx];
                AddPhysBoneKey(offClip, _scannedPhysBones[bi], false);
                AddPhysBoneKey(onClip, _scannedPhysBones[bi], enabled);
            }
            SaveClipAsset(offClip, cat.name + "_OFF");
            SaveClipAsset(onClip, cat.name + "_ON");

            sm.entryPosition    = new Vector3(GCX, GY0, 0);
            sm.anyStatePosition = new Vector3(GCX, GY0 + 40f, 0);
            sm.exitPosition     = new Vector3(GCX, GY0 - 40f, 0);

            string param = cat.ResolvedParameterName;

            // デフォルト = ON（衣装を着ている状態）、パラメーター=true で OFF（脱ぐ）
            var onState = sm.AddState("ON", new Vector3(GX, GY0, 0));
            onState.motion = onClip;
            onState.writeDefaultValues = false;
            sm.defaultState = onState;

            var offState = sm.AddState("OFF", new Vector3(GX, GY0 + GYS, 0));
            offState.motion = offClip;
            offState.writeDefaultValues = false;

            bool needsBodyCheckC = cat.hideOnCastoff || cat.hideOnNaked;
            if (needsBodyCheckC)
            {
                // AnyStateトランジションで優先度制御（hideOnCastoff/hideOnNaked の組み合わせに対応）

                // 優先1: COS_B_HIDE（Body=1、hideOnCastoffが有効な場合）
                if (cat.hideOnCastoff)
                {
                    var cosBHideState = sm.AddState("COS_B_HIDE", new Vector3(GX, GY0 - GYS, 0));
                    cosBHideState.motion = offClip;
                    cosBHideState.writeDefaultValues = false;
                    var tCosB = sm.AddAnyStateTransition(cosBHideState);
                    tCosB.hasExitTime = false; tCosB.duration = 0f; tCosB.canTransitionToSelf = false;
                    tCosB.AddCondition(AnimatorConditionMode.Equals, 1, "CostumeBody");
                }

                // 優先2: NAKED_HIDE（Body=2、hideOnNakedが有効な場合）
                if (cat.hideOnNaked)
                {
                    var nakedHideState = sm.AddState("NAKED_HIDE", new Vector3(GX, GY0 - GYS * 2, 0));
                    nakedHideState.motion = offClip;
                    nakedHideState.writeDefaultValues = false;
                    var tNaked = sm.AddAnyStateTransition(nakedHideState);
                    tNaked.hasExitTime = false; tNaked.duration = 0f; tNaked.canTransitionToSelf = false;
                    tNaked.AddCondition(AnimatorConditionMode.Greater, 1, "CostumeBody");
                }

                // 優先3: param=true（スロットOFF）
                var tOff = sm.AddAnyStateTransition(offState);
                tOff.hasExitTime = false; tOff.duration = 0f; tOff.canTransitionToSelf = false;
                tOff.AddCondition(AnimatorConditionMode.If, 0, param);

                // 優先4: ON（param=false かつ Body が非表示範囲外）
                var tOn = sm.AddAnyStateTransition(onState);
                tOn.hasExitTime = false; tOn.duration = 0f; tOn.canTransitionToSelf = false;
                tOn.AddCondition(AnimatorConditionMode.IfNot, 0, param);
                int onBodyMax = cat.hideOnCastoff ? 1 : 2;
                tOn.AddCondition(AnimatorConditionMode.Less, onBodyMax, "CostumeBody");

                if (!cat.hideOnNaked)
                {
                    // hideOnCastoffのみ: Body=2（キャストオフ）は ON
                    var tOn2 = sm.AddAnyStateTransition(onState);
                    tOn2.hasExitTime = false; tOn2.duration = 0f; tOn2.canTransitionToSelf = false;
                    tOn2.AddCondition(AnimatorConditionMode.IfNot, 0, param);
                    tOn2.AddCondition(AnimatorConditionMode.Greater, 1, "CostumeBody");
                }
            }
            else
            {
                var toOff = onState.AddTransition(offState);
                toOff.hasExitTime = false;
                toOff.duration = 0f;
                toOff.AddCondition(AnimatorConditionMode.If, 0, param);

                var toOn = offState.AddTransition(onState);
                toOn.hasExitTime = false;
                toOn.duration = 0f;
                toOn.AddCondition(AnimatorConditionMode.IfNot, 0, param);
            }
        }

        void BuildBrightnessLayers(AnimatorController controller, string outfitName)
        {
            AddParameterIfMissing(controller, "Brightness", AnimatorControllerParameterType.Float);
            AddParameterIfMissing(controller, "Lighting(mono)", AnimatorControllerParameterType.Float);

            var smrPaths = _prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Select(r => GetRelativePath(r.transform)).ToList();

            var brightnessClip = CreateMaterialFloatClip(
                "Brightness_" + outfitName, smrPaths, "material._LightMinLimit", LilLightMinLimit, 1.0f);
            var monoClip = CreateMaterialFloatClip(
                "MonochromeLighting_" + outfitName, smrPaths, "material._MonochromeLighting", LilMonochromeLighting, 1.0f);

            AddTimeParamLayer(controller, "CFG_Brightness", "BRIGHTNESS_CTRL", "Brightness", brightnessClip);
            AddTimeParamLayer(controller, "CFG_MonochromeLighting", "LIGHTING(MONO)_CTRL", "Lighting(mono)", monoClip);
        }

        AnimationClip CreateMaterialFloatClip(string clipName, List<string> paths, string attribute, float startValue, float endValue)
        {
            var clip = new AnimationClip { name = clipName };
            foreach (var path in paths)
            {
                var k0 = new Keyframe(0f, startValue) { tangentMode = 136, inWeight = 0.33333334f, outWeight = 0.33333334f };
                var k1 = new Keyframe(1f, endValue) { tangentMode = 136, inWeight = 0.33333334f, outWeight = 0.33333334f };
                var curve = new AnimationCurve(k0, k1);
                var binding = EditorCurveBinding.FloatCurve(path, typeof(SkinnedMeshRenderer), attribute);
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
            string assetPath = _outputFolder + "/" + clipName + ".anim";
            if (AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath) != null)
                AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(clip, assetPath);
            return clip;
        }

        void AddTimeParamLayer(AnimatorController controller, string layerName, string stateName, string timeParameter, AnimationClip clip)
        {
            var layer = AddLayer(controller, layerName);
            var sm = layer.stateMachine;
            sm.entryPosition    = new Vector3(GCX, GY0, 0);
            sm.anyStatePosition = new Vector3(GCX, GY0 + 40f, 0);
            sm.exitPosition     = new Vector3(GCX, GY0 - 40f, 0);
            var state = sm.AddState(stateName, new Vector3(GX, GY0, 0));
            state.writeDefaultValues = false;
            state.timeParameterActive = true;
            state.timeParameter = timeParameter;
            state.motion = clip;
            sm.defaultState = state;
        }

        // ---------------- メニュー整形 ----------------

        void ExecuteMenuFormatting()
        {
            try
            {
                if (_prefab == null)
                {
                    EditorUtility.DisplayDialog("エラー", "衣装プレハブを設定してください。", "OK");
                    return;
                }

                if (_hasExistingMenu && _existingMenuHandling == ExistingMenuHandling.KeepAsIs)
                {
                    EditorUtility.DisplayDialog("情報", "既存メニューをそのまま使用します。メニュー整形はスキップされました。", "OK");
                    return;
                }

                if (_hasExistingMenu && _existingMenuHandling == ExistingMenuHandling.ConvertToFormat)
                    ConvertExistingMenu();
                else if (_mode == OutfitMode.CosA)
                    BuildCosAMenu();
                else if (_mode == OutfitMode.Independent)
                    BuildIndependentMenu();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
                EditorUtility.DisplayDialog("完了", "メニュー整形が完了しました。", "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("エラー", "メニュー整形中にエラーが発生しました:\n" + e.Message, "OK");
                Debug.LogException(e);
            }
        }

        Texture2D ResolveIcon(string key)
        {
            if (_existingIcons.TryGetValue(key, out var ex) && ex != null) return ex;
            if (_generatedIcons.TryGetValue(key, out var gen) && gen != null) return gen;
            return null;
        }

        Texture2D FirstGeneratedIcon() => _generatedIcons.Values.FirstOrDefault(t => t != null);

        // NAKEDパターン用のXアイコン（リソースフォルダから読み込み）
        static Texture2D GetDeleteIcon()
            => AssetDatabase.LoadAssetAtPath<Texture2D>(DeleteIconPath);

        List<VRCExpressionsMenu.Control> BuildControlsFromCategories()
        {
            var controls = new List<VRCExpressionsMenu.Control>();
            // キャストオフ（NAKED label）は最後にまとめて追加するため別リストに収集
            var castoffControls = new List<VRCExpressionsMenu.Control>();

            for (int ci = 0; ci < _categories.Count; ci++)
            {
                var cat = _categories[ci];
                var resolved = cat.ResolvedParameterName;
                if (string.IsNullOrEmpty(resolved)) continue;

                var icon = ResolveIcon(IconKey(ci, 0));

                if (cat.blendMode == BlendMode.Combine || cat.parameterType == ParameterType.Bool)
                {
                    controls.Add(new VRCExpressionsMenu.Control
                    {
                        name = "",
                        icon = icon,
                        type = VRCExpressionsMenu.Control.ControlType.Toggle,
                        parameter = new VRCExpressionsMenu.Control.Parameter { name = resolved },
                        value = 1f
                    });
                }
                else
                {
                    for (int pi = 0; pi < cat.patterns.Count; pi++)
                    {
                        var pat = cat.patterns[pi];
                        // NAKED・COS_B・value=0（常時ON置き換え）はメニューに出さない
                        if (pat.isNaked || pat.isCosB || pat.value == 0) continue; // menuOnly はここを通過してメニューに出る
                        var patIcon = ResolveIcon(IconKey(ci, pi)) ?? icon;
                        var ctrl = new VRCExpressionsMenu.Control
                        {
                            name = "",
                            icon = patIcon,
                            type = VRCExpressionsMenu.Control.ControlType.Toggle,
                            parameter = new VRCExpressionsMenu.Control.Parameter { name = resolved },
                            value = pat.value
                        };
                        // isCastoff（キャストオフ）は末尾に配置、常にDELETEアイコン
                        if (pat.isCastoff)
                        {
                            ctrl.icon = GetDeleteIcon();
                            castoffControls.Add(ctrl);
                        }
                        else
                        {
                            controls.Add(ctrl);
                        }
                    }
                }
            }
            // キャストオフを末尾に追加
            controls.AddRange(castoffControls);
            return controls;
        }

        void BuildCosAMenu()
        {
            VRCExpressionsMenu outfitMenu = _cosAOutfitMenu;
            if (outfitMenu == null)
            {
                string targetPath = AssetDatabase.GUIDToAssetPath(TargetMenuGuid);
                if (!string.IsNullOrEmpty(targetPath))
                    outfitMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(targetPath);
            }
            if (outfitMenu == null)
            {
                EditorUtility.DisplayDialog("エラー", "インストール先の OutfitMenu が設定されていません。\nモード選択欄の「インストール先 OutfitMenu」にメニューアセットをセットしてください。", "OK");
                return;
            }

            var controls = BuildControlsFromCategories();
            if (controls.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "カテゴリが設定されていません。カテゴリビルダーで設定してください。", "OK");
                return;
            }

            EnsureOutputFolder();
            var menu = CreateInstance<VRCExpressionsMenu>();
            menu.controls = controls;
            string menuPath = _outputFolder + "/" + _prefab.name + "_Menu.asset";
            if (AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(menuPath) != null)
                AssetDatabase.DeleteAsset(menuPath);
            AssetDatabase.CreateAsset(menu, menuPath);

            var installer = FindOrAddMenuInstaller();
            if (installer == null)
            {
                EditorUtility.DisplayDialog("エラー", "ModularAvatarMenuInstallerを追加できませんでした。Modular Avatarがインポートされているか確認してください。", "OK");
                return;
            }
            var so = new SerializedObject(installer);
            so.FindProperty("menuToAppend").objectReferenceValue = menu;
            so.FindProperty("installTargetMenu").objectReferenceValue = outfitMenu;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(installer);
        }

        void BuildIndependentMenu()
        {
            var controls = BuildControlsFromCategories();
            if (controls.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "カテゴリが設定されていません。カテゴリビルダーで設定してください。", "OK");
                return;
            }

            EnsureOutputFolder();
            var menu = CreateInstance<VRCExpressionsMenu>();
            menu.controls = controls;
            string menuPath = _outputFolder + "/" + _prefab.name + "_Menu.asset";
            if (AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(menuPath) != null)
                AssetDatabase.DeleteAsset(menuPath);
            AssetDatabase.CreateAsset(menu, menuPath);

            var installer = FindOrAddMenuInstaller();
            if (installer == null)
            {
                EditorUtility.DisplayDialog("エラー", "ModularAvatarMenuInstallerを追加できませんでした。Modular Avatarがインポートされているか確認してください。", "OK");
                return;
            }
            var so = new SerializedObject(installer);
            so.FindProperty("menuToAppend").objectReferenceValue = menu;
            so.FindProperty("installTargetMenu").objectReferenceValue = _independentInstallTarget;
            so.ApplyModifiedProperties();
            EditorUtility.SetDirty(installer);
        }

        Component FindOrAddMenuInstaller()
        {
            foreach (var c in _prefab.GetComponentsInChildren<Component>(true))
            {
                if (c != null && (c.GetType().FullName ?? "").Contains("ModularAvatarMenuInstaller"))
                    return c;
            }
            var type = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => { try { return a.GetTypes(); } catch { return Type.EmptyTypes; } })
                .FirstOrDefault(t => (t.FullName ?? "").Contains("ModularAvatarMenuInstaller") && typeof(Component).IsAssignableFrom(t));
            if (type == null) return null;
            return _prefab.AddComponent(type);
        }

        void ConvertExistingMenu()
        {
            Component installer = null;
            foreach (var c in _prefab.GetComponentsInChildren<Component>(true))
            {
                if (c != null && (c.GetType().FullName ?? "").Contains("ModularAvatarMenuInstaller"))
                { installer = c; break; }
            }
            if (installer == null)
            {
                EditorUtility.DisplayDialog("エラー", "変換対象のModularAvatarMenuInstallerが見つかりません。", "OK");
                return;
            }

            var so = new SerializedObject(installer);
            var menuProp = so.FindProperty("menuToAppend");
            var menu = menuProp?.objectReferenceValue as VRCExpressionsMenu;
            if (menu == null)
            {
                EditorUtility.DisplayDialog("エラー", "既存メニュー（menuToAppend）が取得できませんでした。", "OK");
                return;
            }

            foreach (var control in menu.controls)
            {
                control.name = "";
                var paramName = control.parameter?.name ?? "";
                Texture2D resolvedIcon = null;

                // パラメータ名でスロットを逆引きしてアイコンを解決。見つからなければ既存アイコンを維持
                if (_simpleMode)
                {
                    var slots = _simpleOutfitMode == OutfitMode.CosA ? _simpleCosASlots : _simpleCosBSlots;
                    if (slots != null)
                        for (int si = 0; si < slots.Length; si++)
                            if (slots[si].parameterName == paramName)
                            { resolvedIcon = ResolveIcon(IconKey(si, 0)); break; }
                }
                else
                {
                    int ci = _categories.FindIndex(c => c.ResolvedParameterName == paramName);
                    if (ci >= 0)
                        resolvedIcon = ResolveIcon(IconKey(ci, (int)control.value)) ?? ResolveIcon(IconKey(ci, 0));
                }
                if (resolvedIcon != null) control.icon = resolvedIcon;
                // resolvedIcon == null の場合は既存アイコン（DELETEなど）をそのまま維持
            }
            EditorUtility.SetDirty(menu);

            if (_mode == OutfitMode.CosA)
            {
                VRCExpressionsMenu outfitMenu = _cosAOutfitMenu;
                if (outfitMenu == null)
                {
                    string targetPath = AssetDatabase.GUIDToAssetPath(TargetMenuGuid);
                    if (!string.IsNullOrEmpty(targetPath))
                        outfitMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(targetPath);
                }
                if (outfitMenu != null)
                {
                    so.FindProperty("installTargetMenu").objectReferenceValue = outfitMenu;
                    so.ApplyModifiedProperties();
                }
            }
            EditorUtility.SetDirty(installer);
        }

        // ---------------- lilToon同期 ----------------

        void ExecuteLilToonSync()
        {
            try
            {
                if (_prefab == null)
                {
                    EditorUtility.DisplayDialog("エラー", "衣装プレハブを設定してください。", "OK");
                    return;
                }

                // マテリアルのデフォルト値同期
                var materials = _prefab.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null && m.shader != null && m.shader.name.Contains("lilToon"))
                    .Distinct()
                    .ToList();

                if (materials.Count == 0)
                {
                    EditorUtility.DisplayDialog("情報", "lilToonマテリアルが見つかりませんでした。", "OK");
                    return;
                }

                foreach (var mat in materials)
                {
                    if (mat.HasProperty("_LightMinLimit"))
                        mat.SetFloat("_LightMinLimit", LilLightMinLimit);
                    if (mat.HasProperty("_MonochromeLighting"))
                        mat.SetFloat("_MonochromeLighting", LilMonochromeLighting);
                    EditorUtility.SetDirty(mat);
                }

                // Brightness / Lighting(mono) アニメーション生成
                // すでにExecuteAnimationGenerationで生成済みの場合はスキップ
                if (_lastGeneratedController == null)
                {
                    EnsureOutputFolder();
                    string outfitName = _prefab.name;
                    string controllerPath = _outputFolder + "/" + outfitName + "_Brightness.controller";
                    var controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
                    controller.layers = controller.layers.Where(l => l.name != "Base Layer").ToArray();
                    BuildBrightnessLayers(controller, outfitName);
                    _lastGeneratedController = controller;
                    EditorUtility.SetDirty(controller);
                    if (_applyToPrefab) ApplyToPrefab();
                }
                else
                {
                    // 既存コントローラーにレイヤーがなければ追加
                    string outfitName = _prefab.name;
                    bool hasBrightness = _lastGeneratedController.layers.Any(l => l.name == "CFG_Brightness");
                    if (!hasBrightness)
                        BuildBrightnessLayers(_lastGeneratedController, outfitName);
                    EditorUtility.SetDirty(_lastGeneratedController);
                }

                AssetDatabase.SaveAssets();
                EditorUtility.DisplayDialog("完了",
                    materials.Count + "個のlilToonマテリアルを同期し、Brightness/Lighting(mono)レイヤーを生成しました。", "OK");
            }
            catch (Exception e)
            {
                EditorUtility.DisplayDialog("エラー", "lilToon同期中にエラーが発生しました:\n" + e.Message, "OK");
                Debug.LogException(e);
            }
        }
    }
}
#endif
