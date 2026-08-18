#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Events;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;
using UdonSharp;
using UdonSharpEditor;
using VRC.SDK3.Components;
using VRC.SDKBase;
using VRC.Udon;

namespace MyDartsSystem.EditorTools
{
    /// <summary>
    /// マイダーツ拡張のセットアップウィンドウ。
    ///
    /// メニュー: MyDarts → セットアップ
    ///
    /// 仕様が固まるまで何度も作り直す前提なので、操作を4つに分けてある。
    ///   1. 生成 / 作り直す   … 一式を消して新規生成（設定はアセットに残るので入れ直し不要）
    ///   2. 参照を繋ぎ直す     … 階層はそのままで、SerializeFieldの参照だけ再解決
    ///   3. 設定値だけ流し込む … GASのURLや初期パーツ番号だけ更新
    ///   4. 削除              … 生成物を全部消す
    ///
    /// 既存のCrecent側のファイルには一切触らない。
    /// </summary>
    public class MyDartsSetupWindow : EditorWindow
    {
        private const string RootName = "[MyDarts] System";
        // 設定アセットはパッケージ本体の外に置く。
        // Packages/ 配下はパッケージ更新で丸ごと差し替わるため。
        private const string SettingsDir = "Assets/MyDartsSystem Settings";
        private const string SettingsPath = SettingsDir + "/MyDartsSetupSettings.asset";

        // 撮影セットと動画の映し先。設定と同じ場所に置く
        private const string StudioRtPath = SettingsDir + "/MyDartsStudio.renderTexture";
        private const string VideoRtPath = SettingsDir + "/MyDartsVideo.renderTexture";


        // ============================================================
        //  色づかい
        //
        //  黒地に、発光する青と白だけで組む。
        //  背景を深く沈めて、UIが画面で一番明るいものになるようにする。
        //  色数を増やすと締まりが無くなるので、増やさない。
        // ============================================================
        private static readonly Color ColBg = new Color(0.024f, 0.031f, 0.047f, 0.98f);   // ほぼ黒
        private static readonly Color ColBar = new Color(0.043f, 0.063f, 0.098f, 1f);     // 帯
        private static readonly Color ColPanel = new Color(0.055f, 0.086f, 0.129f, 1f);   // ボタンの地
        private static readonly Color ColGlow = new Color(0.208f, 0.776f, 1f, 1f);        // 発光する青
        private static readonly Color ColGlowDim = new Color(0.208f, 0.776f, 1f, 0.35f);  // 弱い青
        private static readonly Color ColWhite = new Color(1f, 1f, 1f, 1f);
        private static readonly Color ColText = new Color(0.878f, 0.929f, 0.973f, 1f);    // 白めの文字
        private static readonly Color ColSub = new Color(0.404f, 0.510f, 0.596f, 1f);     // 添え字
        private static readonly Color ColDeep = new Color(0.086f, 0.290f, 0.588f, 1f);    // 濃い青

        // レア度。青と白の中で段階を作る
        private static readonly Color ColCommon = new Color(0.784f, 0.831f, 0.867f, 1f);
        private static readonly Color ColRare = new Color(0.208f, 0.776f, 1f, 1f);
        private static readonly Color ColVeryRare = new Color(0.298f, 0.486f, 1f, 1f);
        private static readonly Color ColUltra = new Color(1f, 1f, 1f, 1f);

        private MyDartsSetupSettings _settings;
        private SerializedObject _so;
        private Vector2 _scroll;

        // 生成中に「指でも押せるようにするボタン」を集めておく入れ物。
        // ポインタ用の割り当てと同じ相手・同じ合図を、指用にも入れるため。
        private readonly List<Button> _touchButtons = new List<Button>();
        private readonly List<UdonSharpBehaviour> _touchTargets = new List<UdonSharpBehaviour>();
        private readonly List<string> _touchEvents = new List<string>();

        /// <summary>
        /// まとめ画面から中身だけ borrowed される。
        /// 単体のウィンドウとしては開かない（入口は MyDarts → マイダーツ）。
        /// </summary>
        public static MyDartsSetupWindow CreateEmbedded()
        {
            var w = CreateInstance<MyDartsSetupWindow>();
            w.LoadOrCreateSettings();
            return w;
        }

        private void OnEnable()
        {
            LoadOrCreateSettings();
        }

        /// <summary>まとめ画面から設定値を読む</summary>
        public MyDartsSetupSettings GetSettings()
        {
            if (_settings == null) LoadOrCreateSettings();
            return _settings;
        }

        /// <summary>まとめ画面で設定値を変えたときに呼ぶ</summary>
        public void SaveSettings()
        {
            if (_settings == null) return;
            EditorUtility.SetDirty(_settings);
            AssetDatabase.SaveAssets();
            if (_so != null) _so.Update();
        }

        /// <summary>生成済みか</summary>
        public bool HasRoot() { return FindRoot() != null; }

        /// <summary>
        /// 昔の作りのまま残っているかどうか。
        ///
        /// 画面が別々のCanvasに分かれていた頃のものは、
        /// 繋ぎ直しでは筐体の形にならない。作り直しが要る。
        /// </summary>
        public bool NeedsRebuild()
        {
            var root = FindRoot();
            if (root == null) return false;

            if (root.GetComponentInChildren<MyDartsScreens>(true) == null) return true;
            if (root.GetComponentInChildren<MyDartsTouchInput>(true) == null) return true;
            if (root.GetComponentInChildren<MyDartsGachaStage>(true) == null) return true;
            if (FindChildByName(root.transform, "CabinetCanvas") == null) return true;
            if (FindChildByName(root.transform, "Page0_Top") == null) return true;

            return false;
        }

        // ============================================================
        //  ウィンドウ描画
        // ============================================================
        private void OnGUI() { DrawGUI(); }

        /// <summary>まとめ画面からも呼べるように、中身を切り出してある</summary>
        public void DrawGUI()
        {
            if (_settings == null) LoadOrCreateSettings();
            if (_so == null) return;

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("マイダーツ拡張 セットアップ", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "既存の Crecent Darts には一切手を加えません。\n" +
                "生成物はすべて「" + RootName + "」の下にまとまります。",
                MessageType.Info);

            DrawLine();

            _so.Update();

            EditorGUILayout.LabelField("接続先", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_so.FindProperty("gasBaseUrl"), new GUIContent("送信先URL"));
            EditorGUILayout.LabelField(
                "GASは302リダイレクトするためVRChatから直接叩けない。中継のURLを入れる。",
                EditorStyles.miniLabel);

            EditorGUILayout.PropertyField(_so.FindProperty("ledgerUrl"), new GUIContent("台帳URL（読込）"));
            EditorGUILayout.LabelField(
                "空なら「送信先URL + ?all=1」を使う。GitHub Pagesに移すときはここを差し替える。",
                EditorStyles.miniLabel);

            var url = _settings.gasBaseUrl;
            if (string.IsNullOrEmpty(url) || url.Contains("XXXXXXXX"))
            {
                EditorGUILayout.HelpBox("GASのURLがまだ仮のままです。デプロイ後のURLに差し替えてください。", MessageType.Warning);
            }
            else if (!url.EndsWith("/exec") && !url.Contains("workers.dev"))
            {
                EditorGUILayout.HelpBox(
                    "GASの /exec か、中継（Cloudflare Workers等）のURLを入れてください。\n" +
                    "GASの /exec を直接指定すると、VRChatがリダイレクトを追えず " +
                    "\"Redirect limit exceeded\" で失敗します。",
                    MessageType.Warning);
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("登録時の初期パーツ", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_so.FindProperty("initialTip"), new GUIContent("チップ"));
            EditorGUILayout.PropertyField(_so.FindProperty("initialBarrel"), new GUIContent("バレル"));
            EditorGUILayout.PropertyField(_so.FindProperty("initialShaft"), new GUIContent("シャフト"));
            EditorGUILayout.PropertyField(_so.FindProperty("initialFlight"), new GUIContent("フライト"));
            EditorGUILayout.PropertyField(_so.FindProperty("initialFlightShape"), new GUIContent("フライト形状"));

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("配置", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_so.FindProperty("playerPanelPosition"), new GUIContent("プレイヤー用パネル"));
            EditorGUILayout.PropertyField(_so.FindProperty("debugPanelPosition"), new GUIContent("デバッグパネル"));
            EditorGUILayout.PropertyField(_so.FindProperty("panelSizeMeters"), new GUIContent("パネルの大きさ(m)"));
            EditorGUILayout.PropertyField(_so.FindProperty("debugPanelHiddenByDefault"), new GUIContent("デバッグを非表示で生成"));
            EditorGUILayout.PropertyField(_so.FindProperty("debugMaxLines"), new GUIContent("ログ保持行数"));

            _so.ApplyModifiedProperties();

            DrawLine();

            var root = FindRoot();
            EditorGUILayout.LabelField("状態", EditorStyles.boldLabel);
            if (root == null)
            {
                EditorGUILayout.HelpBox("まだシーンに生成されていません。", MessageType.None);
            }
            else
            {
                EditorGUILayout.HelpBox("生成済みです: " + root.name, MessageType.None);
            }

            EditorGUILayout.Space(8);

            // ---- 1. 生成 ----
            GUI.backgroundColor = new Color(0.6f, 0.9f, 1f);
            if (GUILayout.Button(root == null ? "生成する" : "作り直す（消してから生成）", GUILayout.Height(34)))
            {
                Generate();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);

            // ---- 2. 参照を繋ぎ直す ----
            using (new EditorGUI.DisabledScope(root == null))
            {
                if (GUILayout.Button("参照を繋ぎ直す（階層はそのまま）", GUILayout.Height(26)))
                {
                    Relink(root);
                }

                // ---- 3. 設定値だけ ----
                if (GUILayout.Button("設定値だけ流し込む（URL・初期パーツ）", GUILayout.Height(26)))
                {
                    PushSettings(root);
                }
            }

            EditorGUILayout.Space(10);

            using (new EditorGUI.DisabledScope(root == null))
            {
                GUI.backgroundColor = new Color(1f, 0.7f, 0.7f);
                if (GUILayout.Button("削除する"))
                {
                    if (EditorUtility.DisplayDialog("確認", "生成した一式を削除します。よろしいですか？", "削除", "やめる"))
                    {
                        Undo.DestroyObjectImmediate(root);

                        // 消したスクリプトのネットワーク記録も片づける。
                        // 残すと次の再生で「記録にあるのに実物が無い」と言われて止まる
                        MyDartsPartsSetupWindow.CleanNetworkIDsQuiet();
                    }
                }
                GUI.backgroundColor = Color.white;
            }

            EditorGUILayout.Space(10);
            EditorGUILayout.HelpBox(
                "仕様を変えたあとは「参照を繋ぎ直す」を押せば、\n" +
                "位置や見た目の調整を保ったまま参照だけ直せます。\n" +
                "スクリプトのフィールドを増やしたときもこれでOKです。",
                MessageType.None);

            EditorGUILayout.EndScrollView();
        }

        private static void DrawLine()
        {
            EditorGUILayout.Space(6);
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 1f));
            EditorGUILayout.Space(6);
        }

        // ============================================================
        //  設定アセット
        // ============================================================
        private void LoadOrCreateSettings()
        {
            // 置き場所は決め打ちにしない。
            // VCC配布時、本体は Packages/ 配下（更新で上書きされる）に入るので、
            // 設定だけはユーザーの Assets/ に置いて生き残らせる必要がある。
            // 過去バージョンで別の場所に作られていても拾えるよう、まず全体を検索する。
            _settings = FindExistingSettings();

            if (_settings == null)
            {
                if (!Directory.Exists(SettingsDir))
                {
                    Directory.CreateDirectory(SettingsDir);
                    AssetDatabase.Refresh();
                }
                _settings = CreateInstance<MyDartsSetupSettings>();
                AssetDatabase.CreateAsset(_settings, SettingsPath);
                AssetDatabase.SaveAssets();
                Debug.Log("[MyDarts] 設定アセットを作成しました: " + SettingsPath);
            }

            _so = new SerializedObject(_settings);
        }

        /// <summary>
        /// 既存の設定アセットを探す。
        /// 既定の場所 → プロジェクト全体、の順で見る。
        /// 複数見つかった場合は最初のものを使い、警告を出す。
        /// </summary>
        private static MyDartsSetupSettings FindExistingSettings()
        {
            var direct = AssetDatabase.LoadAssetAtPath<MyDartsSetupSettings>(SettingsPath);
            if (direct != null) return direct;

            var guids = AssetDatabase.FindAssets("t:MyDartsSetupSettings");
            if (guids == null || guids.Length == 0) return null;

            if (guids.Length > 1)
            {
                Debug.LogWarning("[MyDarts] 設定アセットが " + guids.Length + " 個あります。最初の1つを使います。");
            }

            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<MyDartsSetupSettings>(path);
        }

        private static GameObject FindRoot()
        {
            return GameObject.Find(RootName);
        }

        // ============================================================
        //  生成
        // ============================================================
        /// <summary>まとめ画面の「セットアップ」から呼ぶ</summary>
        public void GenerateAll() { Generate(); }

        private void Generate()
        {
            _touchButtons.Clear();
            _touchTargets.Clear();
            _touchEvents.Clear();

            var old = FindRoot();
            if (old != null)
            {
                Undo.DestroyObjectImmediate(old);

                // 消したスクリプトのネットワーク記録も片づける。
                // 残すと次の再生で「記録にあるのに実物が無い」と言われて止まる
                MyDartsPartsSetupWindow.CleanNetworkIDsQuiet();
            }

            var root = new GameObject(RootName);
            Undo.RegisterCreatedObjectUndo(root, "Create MyDarts Extension");

            // ---------- マネージャ ----------
            var managers = NewChild(root.transform, "Managers");
            var registerGo = NewChild(managers.transform, "Register");
            var register = registerGo.AddUdonSharpComponent<MyDartsRegister>();

            var fetcherGo = NewChild(managers.transform, "Fetcher");
            var fetcher = fetcherGo.AddUdonSharpComponent<MyDartsFetcher>();

            var bridgeGo = NewChild(managers.transform, "CrecentBridge");
            var bridge = bridgeGo.AddUdonSharpComponent<MyDartsCrecentBridge>();

            var senderGo = NewChild(managers.transform, "Sender");
            var sender = senderGo.AddUdonSharpComponent<MyDartsSender>();

            // 表示を書き換える仕掛けは、画面が隠れても動き続けるよう Managers 側に置く
            var gachaPanelGo = NewChild(managers.transform, "GachaPanel");
            var gachaPanel = gachaPanelGo.AddUdonSharpComponent<MyDartsGachaPanel>();

            var collectionGo = NewChild(managers.transform, "CollectionPanel");
            var collectionPanel = collectionGo.AddUdonSharpComponent<MyDartsCollectionPanel>();

            // ---------- デバッグパネル ----------
            var debugCanvas = CreateWorldCanvas(root.transform, "DebugCanvas",
                _settings.debugPanelPosition, _settings.panelSizeMeters);

            var debugPanelGo = CreatePanel(debugCanvas.transform, "Panel", new Color(0.05f, 0.05f, 0.06f, 0.95f));
            var debug = debugPanelGo.AddUdonSharpComponent<MyDartsDebugPanel>();

            var debugTitle = CreateText(debugPanelGo.transform, "Title", "DEBUG - not for players", 24, TextAlignmentOptions.Left);
            debugTitle.color = new Color(1f, 0.85f, 0.3f, 1f);
            SetTopRect(debugTitle.rectTransform, 16, 36, 20);

            var state = CreateText(debugPanelGo.transform, "State", "Idle", 30, TextAlignmentOptions.Left);
            state.color = new Color(0.4f, 1f, 0.9f, 1f);
            SetTopRect(state.rectTransform, 56, 44, 20);

            var log = CreateText(debugPanelGo.transform, "Log", "", 18, TextAlignmentOptions.TopLeft);
            log.color = new Color(0.85f, 0.88f, 0.9f, 1f);
            // 下側はテストボタンに譲る
            SetAnchors(log.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(20, 116), new Vector2(-20, -104));

            // テスト用の増減ボタン。プレイヤーには見せないのでデバッグ側に置く
            var addButton = CreateButton(debugPanelGo.transform, "TestAddCoin", "TEST  +Coins");
            SetBottomHalfRect(addButton.GetComponent<RectTransform>(), 24, 74, true);
            addButton.targetGraphic.color = new Color(0.2f, 0.6f, 0.35f, 1f);

            var subButton = CreateButton(debugPanelGo.transform, "TestSubCoin", "TEST  -Coins");
            SetBottomHalfRect(subButton.GetComponent<RectTransform>(), 24, 74, false);
            subButton.targetGraphic.color = new Color(0.65f, 0.28f, 0.28f, 1f);

            // ---------- テスト用パーツパネル ----------
            var testCanvas = CreateWorldCanvas(root.transform, "TestCanvas",
                _settings.debugPanelPosition + new Vector3(1.6f, 0f, 0f), _settings.panelSizeMeters);

            var testPanelGo = CreatePanel(testCanvas.transform, "Panel", new Color(0.06f, 0.07f, 0.05f, 0.95f));
            var tester = testPanelGo.AddUdonSharpComponent<MyDartsPartsTester>();

            var testTitle = CreateText(testPanelGo.transform, "Title", "TEST - parts", 24, TextAlignmentOptions.Left);
            testTitle.color = new Color(0.7f, 1f, 0.5f, 1f);
            SetTopRect(testTitle.rectTransform, 16, 36, 20);

            var currentText = CreateText(testPanelGo.transform, "Current", "Now: -", 20, TextAlignmentOptions.Left);
            currentText.color = new Color(0.85f, 0.9f, 0.85f, 1f);
            SetTopRect(currentText.rectTransform, 56, 34, 20);

            var partNames = new string[] { "Tip", "Barrel", "Shaft", "Flight", "Shape" };
            var partEvents = new string[] { "OnClickTip", "OnClickBarrel", "OnClickShaft", "OnClickFlight", "OnClickShape" };
            var partFields = new TMP_InputField[5];
            var partButtons = new Button[5];
            var priceLabels = new TextMeshProUGUI[5];

            for (var i = 0; i < partNames.Length; i++)
            {
                var top = 104f + i * 82f;

                var label = CreateText(testPanelGo.transform, partNames[i] + "Label", partNames[i], 22, TextAlignmentOptions.Left);
                SetRowRect(label.rectTransform, top, 60, 0f, 0.30f);

                partFields[i] = CreateInputField(testPanelGo.transform, partNames[i] + "Field", "0", true);
                SetRowRect(partFields[i].GetComponent<RectTransform>(), top, 60, 0.26f, 0.52f);

                priceLabels[i] = CreateText(testPanelGo.transform, partNames[i] + "Price", "-", 22, TextAlignmentOptions.Right);
                SetRowRect(priceLabels[i].rectTransform, top, 60, 0.52f, 0.74f);

                partButtons[i] = CreateButton(testPanelGo.transform, partNames[i] + "Button", "ADD");
                SetRowRect(partButtons[i].GetComponent<RectTransform>(), top, 60, 0.74f, 1f);
                partButtons[i].targetGraphic.color = new Color(0.25f, 0.5f, 0.7f, 1f);

                BindButton(partButtons[i], tester, partEvents[i]);
            }

            // ---- カートと購入 ----
            var cartText = CreateText(testPanelGo.transform, "Cart", "Cart: empty", 20, TextAlignmentOptions.Left);
            SetTopRect(cartText.rectTransform, 104f + partNames.Length * 82f + 10f, 40, 20);

            var buyButton = CreateButton(testPanelGo.transform, "BuyButton", "BUY ALL");
            SetBottomHalfRect(buyButton.GetComponent<RectTransform>(), 24, 74, true);
            buyButton.targetGraphic.color = new Color(0.85f, 0.6f, 0.15f, 1f);
            BindButton(buyButton, tester, "OnClickBuy");

            var clearButton = CreateButton(testPanelGo.transform, "ClearButton", "CLEAR");
            SetBottomHalfRect(clearButton.GetComponent<RectTransform>(), 24, 74, false);
            clearButton.targetGraphic.color = new Color(0.4f, 0.4f, 0.45f, 1f);
            BindButton(clearButton, tester, "OnClickClear");


            // ============================================================
            //  筐体（プレイヤーが触る画面）
            //
            //  ゲームセンターの機械のように、1枚のCanvasの中で画面を切り替える。
            //  画面はどれも同じ場所に重ねて置いてあり、
            //  表示する1枚だけを残して他を隠す（MyDartsScreens が担当）。
            //
            //    トップ → メニュー ┬ ガチャ ┬ ノーマル
            //                      │        └ ピックアップ
            //                      ├ マイダーツ
            //                      └ クレジット
            //
            //  画面を切り替える仕掛け（Screens）と、表示を書き換える仕掛け
            //  （ガチャ画面・コレクション画面）は Managers 側に置いてある。
            //  画面そのものは隠れるので、隠れた側に置くと更新が止まってしまうため。
            // ============================================================
            const float HeaderH = 96f;
            const float NavH = 96f;

            var cabinetSize = _settings.cabinetSizeMeters;
            if (cabinetSize.x < 0.1f || cabinetSize.y < 0.1f) cabinetSize = new Vector2(1.2f, 1.6f);

            var cabinetCanvas = CreateWorldCanvas(root.transform, "CabinetCanvas",
                _settings.playerPanelPosition, cabinetSize);

            var frame = CreatePanel(cabinetCanvas.transform, "Frame", ColBg);

            // UIの後ろに敷く動画の背景。Canvasは子の並び順が描画順なので、
            // ここで一番先に作れば、あとから作る画面はすべてこの上に乗る
            var videoBack = CreateRawImage(frame.transform, "VideoBack", null);
            SetAnchors(videoBack.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            videoBack.gameObject.SetActive(false);

            var screensGo = NewChild(managers.transform, "Screens");
            var screens = screensGo.AddUdonSharpComponent<MyDartsScreens>();

            // 指で触って押すための仕掛け。VRの人だけ動く
            var touchGo = NewChild(managers.transform, "TouchInput");
            var touchInput = touchGo.AddUdonSharpComponent<MyDartsTouchInput>();

            // ガチャ演出の進行役。画面より先に作っておく（ボタンの行き先になるため）
            var studioRt = EnsureRenderTexture(StudioRtPath, 512, 512);
            var videoRt = EnsureRenderTexture(VideoRtPath, 1080, 1280);
            EnsureAlphaMaterial();

            var stageGo = NewChild(managers.transform, "GachaStage");
            var stage = stageGo.AddUdonSharpComponent<MyDartsGachaStage>();

            var statusGo = NewChild(managers.transform, "Status");
            var status = statusGo.AddUdonSharpComponent<MyDartsStatusPanel>();

            // ---------- 上の帯（どの画面でも出しっぱなし） ----------
            var header = NewChild(frame.transform, "Header");
            var headerRt = header.AddComponent<RectTransform>();
            SetAnchors(headerRt, new Vector2(0f, 1f), new Vector2(1f, 1f),
                       new Vector2(0f, -HeaderH), Vector2.zero);

            var headerBg = header.AddComponent<Image>();
            headerBg.color = ColBar;
            headerBg.raycastTarget = false;

            var headerTitle = CreateText(header.transform, "HeaderTitle", "MY DARTS", 34, TextAlignmentOptions.Left);
            headerTitle.color = ColGlow;
            SetAnchors(headerTitle.rectTransform, Vector2.zero, new Vector2(0.5f, 1f),
                       new Vector2(28f, 0f), Vector2.zero);

            var creditText = CreateText(header.transform, "CreditText", "CREDIT  -", 34, TextAlignmentOptions.Right);
            creditText.color = ColWhite;
            SetAnchors(creditText.rectTransform, new Vector2(0.5f, 0f), Vector2.one,
                       Vector2.zero, new Vector2(-28f, 0f));

            // ---------- 下の帯（戻る・トップへ） ----------
            var nav = NewChild(frame.transform, "Nav");
            var navRt = nav.AddComponent<RectTransform>();
            SetAnchors(navRt, Vector2.zero, new Vector2(1f, 0f), Vector2.zero, new Vector2(0f, NavH));

            var navBg = nav.AddComponent<Image>();
            navBg.color = ColBar;
            navBg.raycastTarget = false;

            var backButton = CreateButton(nav.transform, "BackButton", "<  BACK");
            SetAnchors(backButton.GetComponent<RectTransform>(), Vector2.zero, new Vector2(0.32f, 1f),
                       new Vector2(20f, 14f), new Vector2(-8f, -14f));
            StyleButton(backButton, false);

            var homeButton = CreateButton(nav.transform, "HomeButton", "HOME");
            SetAnchors(homeButton.GetComponent<RectTransform>(), new Vector2(0.68f, 0f), Vector2.one,
                       new Vector2(8f, 14f), new Vector2(-20f, -14f));
            StyleButton(homeButton, false);

            // ---------- 画面の置き場 ----------
            var pagesRoot = NewChild(frame.transform, "Pages");
            var pagesRt = pagesRoot.AddComponent<RectTransform>();
            SetAnchors(pagesRt, Vector2.zero, Vector2.one, new Vector2(0f, NavH), new Vector2(0f, -HeaderH));

            // 画面を滑らせた時、枠の外へ出た部分を切り落とす。
            // これが無いと、動いている最中に帯の上や外へはみ出して見える
            pagesRoot.AddComponent<RectMask2D>();

            // ============================================================
            //  ① トップ
            // ============================================================
            var pageTop = CreatePage(pagesRoot.transform, "Page0_Top");

            var bigTitle = CreateText(pageTop.transform, "BigTitle", "MY DARTS\nSYSTEM", 86, TextAlignmentOptions.Center);
            bigTitle.color = ColWhite;
            SetTopRect(bigTitle.rectTransform, 44, 220);

            var subTitle = CreateText(pageTop.transform, "SubTitle", "ARCADE  CUSTOM  DARTS", 30, TextAlignmentOptions.Center);
            subTitle.color = ColGlow;
            SetTopRect(subTitle.rectTransform, 268, 44);

            var topHint = CreateText(pageTop.transform, "TopHint", "Press START", 30, TextAlignmentOptions.Center);
            topHint.color = ColSub;
            SetTopRect(topHint.rectTransform, 322, 44);

            var playButton = CreateButton(pageTop.transform, "PlayButton", "PLAY");
            SetRowRect(playButton.GetComponent<RectTransform>(), 386, 150, 0.20f, 0.80f, 0f);
            StyleButton(playButton, true);
            var playLabel = playButton.transform.Find("Label");
            if (playLabel != null)
            {
                var pl = playLabel.GetComponent<TextMeshProUGUI>();
                if (pl != null) pl.fontSize = 56;
            }

            var title = CreateText(pageTop.transform, "Title", "MyDarts - Not Registered", 36, TextAlignmentOptions.Center);
            SetTopRect(title.rectTransform, 566, 52);

            var body = CreateText(pageTop.transform, "Body",
                "Press \"Build Register URL\" to get started.", 24, TextAlignmentOptions.Center);
            body.color = ColSub;
            SetTopRect(body.rectTransform, 622, 80);

            // 登録UIは1つの入れ物にまとめる。登録済みなら丸ごと隠せるようにするため
            var registerGroup = NewChild(pageTop.transform, "RegisterGroup");
            var rgRt = registerGroup.AddComponent<RectTransform>();
            SetAnchors(rgRt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            var buildButton = CreateButton(registerGroup.transform, "BuildUrlButton", "Build Register URL");
            SetTopRect(buildButton.GetComponent<RectTransform>(), 716, 74);

            var outLabel = CreateText(registerGroup.transform, "OutputLabel", "1. Copy this URL", 20, TextAlignmentOptions.Left);
            SetTopRect(outLabel.rectTransform, 802, 30);

            var urlOutput = CreateInputField(registerGroup.transform, "UrlOutput", "(the URL will appear here)", false);
            SetTopRect(urlOutput.GetComponent<RectTransform>(), 834, 64);

            var inLabel = CreateText(registerGroup.transform, "InputLabel", "2. Paste it here and press Enter", 20, TextAlignmentOptions.Left);
            SetTopRect(inLabel.rectTransform, 908, 30);

            var urlInput = CreateVrcUrlInputField(registerGroup.transform, "UrlInput");
            SetTopRect(urlInput.GetComponent<RectTransform>(), 940, 64);

            var sendButton = CreateButton(registerGroup.transform, "SendButton", "3. Send");
            SetTopRect(sendButton.GetComponent<RectTransform>(), 1016, 74);
            StyleButton(sendButton, true);

            // ============================================================
            //  ② メインメニュー
            // ============================================================
            var pageMenu = CreatePage(pagesRoot.transform, "Page1_Menu");

            var menuTitle = CreateText(pageMenu.transform, "MenuTitle", "MENU", 56, TextAlignmentOptions.Center);
            menuTitle.color = ColGlow;
            SetTopRect(menuTitle.rectTransform, 40, 80);

            var menuNames = new string[] { "GACHA", "MY DARTS", "CREDIT" };
            var menuDescs = new string[] { "Pull for designs", "Check & customize", "Your credits" };
            // 3つとも青。色で区別させず、大きさと文字で区別させる
            var menuButtons = new Button[3];

            for (var i = 0; i < 3; i++)
            {
                var xMin = 0.04f + i * 0.32f;
                var xMax = xMin + 0.28f;

                menuButtons[i] = CreateButton(pageMenu.transform, "Menu" + i + "Button", menuNames[i]);
                SetRowRect(menuButtons[i].GetComponent<RectTransform>(), 220, 460, xMin, xMax, 6f);
                StyleButton(menuButtons[i], i == 0);

                var lb = menuButtons[i].transform.Find("Label");
                if (lb != null)
                {
                    var t = lb.GetComponent<TextMeshProUGUI>();
                    if (t != null) t.fontSize = 40;
                }

                var desc = CreateText(pageMenu.transform, "Menu" + i + "Desc", menuDescs[i], 22, TextAlignmentOptions.Center);
                desc.color = ColSub;
                SetRowRect(desc.rectTransform, 692, 40, xMin, xMax, 6f);
            }

            // ============================================================
            //  ③ ガチャ（種類えらび）
            // ============================================================
            var pageGacha = CreatePage(pagesRoot.transform, "Page2_Gacha");

            var gachaTopTitle = CreateText(pageGacha.transform, "GachaTitle", "GACHA", 56, TextAlignmentOptions.Center);
            gachaTopTitle.color = ColGlow;
            SetTopRect(gachaTopTitle.rectTransform, 40, 80);

            var normalButton = CreateButton(pageGacha.transform, "GachaNormalButton", "NORMAL");
            SetRowRect(normalButton.GetComponent<RectTransform>(), 220, 400, 0.06f, 0.48f, 6f);
            StyleButton(normalButton, true);

            var normalDesc = CreateText(pageGacha.transform, "GachaNormalDesc",
                "Always available", 22, TextAlignmentOptions.Center);
            normalDesc.color = ColSub;
            SetRowRect(normalDesc.rectTransform, 632, 40, 0.06f, 0.48f, 6f);

            var pickupButton = CreateButton(pageGacha.transform, "GachaPickupButton", "PICKUP");
            SetRowRect(pickupButton.GetComponent<RectTransform>(), 220, 400, 0.52f, 0.94f, 6f);
            StyleButton(pickupButton, false);

            var pickupDesc = CreateText(pageGacha.transform, "GachaPickupDesc",
                "Limited banners", 22, TextAlignmentOptions.Center);
            pickupDesc.color = ColSub;
            SetRowRect(pickupDesc.rectTransform, 632, 40, 0.52f, 0.94f, 6f);

            // ============================================================
            //  ④ ノーマルガチャ
            // ============================================================
            var pageGachaNormal = CreatePage(pagesRoot.transform, "Page3_GachaNormal");

            var gnTitle = CreateText(pageGachaNormal.transform, "NormalTitle", "NORMAL GACHA", 46, TextAlignmentOptions.Left);
            gnTitle.color = ColGlow;
            SetTopRect(gnTitle.rectTransform, 30, 62);

            var gachaInfo = CreateText(pageGachaNormal.transform, "Info",
                "SINGLE     one part gets a random design\nFULL SET   all four parts match", 24, TextAlignmentOptions.TopLeft);
            gachaInfo.color = ColSub;
            SetTopRect(gachaInfo.rectTransform, 104, 80);

            var gachaOne = CreateButton(pageGachaNormal.transform, "GachaOneButton", "SINGLE");
            SetRowRect(gachaOne.GetComponent<RectTransform>(), 208, 120, 0f, 0.70f, 24f);
            StyleButton(gachaOne, true);
            BindTouch(gachaOne, stage, "OnPullOne");

            var priceOne = CreateText(pageGachaNormal.transform, "PriceOne", "-", 38, TextAlignmentOptions.Right);
            SetRowRect(priceOne.rectTransform, 208, 120, 0.70f, 1f, 24f);

            var gachaSet = CreateButton(pageGachaNormal.transform, "GachaSetButton", "FULL SET");
            SetRowRect(gachaSet.GetComponent<RectTransform>(), 348, 120, 0f, 0.70f, 24f);
            StyleButton(gachaSet, true);
            BindTouch(gachaSet, stage, "OnPullSet");

            var priceSet = CreateText(pageGachaNormal.transform, "PriceSet", "-", 38, TextAlignmentOptions.Right);
            SetRowRect(priceSet.rectTransform, 348, 120, 0.70f, 1f, 24f);

            var resultTitle = CreateText(pageGachaNormal.transform, "ResultTitle", "-", 54, TextAlignmentOptions.Center);
            SetTopRect(resultTitle.rectTransform, 560, 76);

            var resultBody = CreateText(pageGachaNormal.transform, "ResultBody", "Pull to get a new design.", 30, TextAlignmentOptions.Center);
            SetTopRect(resultBody.rectTransform, 644, 60);

            var collectionText = CreateText(pageGachaNormal.transform, "Collection", "Collected   -", 24, TextAlignmentOptions.Left);
            collectionText.color = ColSub;
            SetTopRect(collectionText.rectTransform, 780, 50);

            // ============================================================
            //  ⑤ ピックアップガチャ（まだ中身なし）
            // ============================================================
            var pageGachaPickup = CreatePage(pagesRoot.transform, "Page4_GachaPickup");

            var puTitle = CreateText(pageGachaPickup.transform, "PickupTitle", "PICKUP GACHA", 46, TextAlignmentOptions.Left);
            puTitle.color = ColGlow;
            SetTopRect(puTitle.rectTransform, 30, 62);

            var puSoon = CreateText(pageGachaPickup.transform, "PickupSoon", "COMING SOON", 56, TextAlignmentOptions.Center);
            puSoon.color = ColSub;
            SetTopRect(puSoon.rectTransform, 340, 90);

            var puNote = CreateText(pageGachaPickup.transform, "PickupNote",
                "Limited banners will show up here.", 26, TextAlignmentOptions.Center);
            puNote.color = ColSub;
            SetTopRect(puNote.rectTransform, 440, 50);

            // ============================================================
            //  ⑥ マイダーツ（持ちもの確認・着せ替え）
            // ============================================================
            var pageMyDarts = CreatePage(pagesRoot.transform, "Page5_MyDarts");

            var colTitle = CreateText(pageMyDarts.transform, "MyDartsTitle", "MY DARTS", 46, TextAlignmentOptions.Left);
            colTitle.color = ColGlow;
            SetTopRect(colTitle.rectTransform, 22, 60);

            var colHint = CreateText(pageMyDarts.transform, "MyDartsHint",
                "[ ] = equipped     * = not saved yet", 22, TextAlignmentOptions.Right);
            colHint.color = ColSub;
            SetTopRect(colHint.rectTransform, 34, 40);

            var colNames = new string[] { "Tip", "Barrel", "Shaft", "Flight" };
            var colPrevEvents = new string[] { "OnPrevTip", "OnPrevBarrel", "OnPrevShaft", "OnPrevFlight" };
            var colNextEvents = new string[] { "OnNextTip", "OnNextBarrel", "OnNextShaft", "OnNextFlight" };

            var countTexts = new TextMeshProUGUI[4];
            var listTexts = new TextMeshProUGUI[4];
            var selectTexts = new TextMeshProUGUI[4];

            for (var i = 0; i < 4; i++)
            {
                var rowTop = 96f + i * 152f;

                var nameLabel = CreateText(pageMyDarts.transform, colNames[i] + "Name",
                    colNames[i].ToUpperInvariant(), 30, TextAlignmentOptions.Left);
                SetRowRect(nameLabel.rectTransform, rowTop, 46, 0f, 0.20f, 24f);

                countTexts[i] = CreateText(pageMyDarts.transform, colNames[i] + "Count",
                    "- / -", 26, TextAlignmentOptions.Left);
                countTexts[i].color = ColSub;
                SetRowRect(countTexts[i].rectTransform, rowTop, 46, 0.20f, 0.44f, 8f);

                var prev = CreateButton(pageMyDarts.transform, colNames[i] + "Prev", "<");
                SetRowRect(prev.GetComponent<RectTransform>(), rowTop, 46, 0.62f, 0.72f, 6f);
                StyleButton(prev, false);
                BindTouch(prev, collectionPanel, colPrevEvents[i]);

                selectTexts[i] = CreateText(pageMyDarts.transform, colNames[i] + "Sel",
                    "-", 32, TextAlignmentOptions.Center);
                SetRowRect(selectTexts[i].rectTransform, rowTop, 46, 0.72f, 0.86f, 4f);

                var next = CreateButton(pageMyDarts.transform, colNames[i] + "Next", ">");
                SetRowRect(next.GetComponent<RectTransform>(), rowTop, 46, 0.86f, 0.98f, 6f);
                StyleButton(next, false);
                BindTouch(next, collectionPanel, colNextEvents[i]);

                listTexts[i] = CreateText(pageMyDarts.transform, colNames[i] + "Owned",
                    "-", 22, TextAlignmentOptions.TopLeft);
                SetTopRect(listTexts[i].rectTransform, rowTop + 50f, 96f);
            }

            // ---- DefaultColor（所持とは関係なく誰でも選べる） ----
            var colorLabel = CreateText(pageMyDarts.transform, "ColorLabel",
                "DEFAULT COLOR", 26, TextAlignmentOptions.Left);
            colorLabel.color = ColGlow;
            SetTopRect(colorLabel.rectTransform, 716, 40);

            var colorText = CreateText(pageMyDarts.transform, "ColorText", "-", 24, TextAlignmentOptions.Right);
            colorText.color = ColSub;
            SetTopRect(colorText.rectTransform, 716, 40);

            var colorPresets = DefaultColorPresets();
            var colorSwatches = new Image[colorPresets.Length];

            for (var i = 0; i < colorPresets.Length; i++)
            {
                var xMin = 0.02f + i * 0.1225f;
                var xMax = xMin + 0.1125f;

                var swatch = CreateButton(pageMyDarts.transform, "Color" + i, "");
                SetRowRect(swatch.GetComponent<RectTransform>(), 762, 82, xMin, xMax, 5f);
                swatch.targetGraphic.color = colorPresets[i];
                colorSwatches[i] = (Image)swatch.targetGraphic;

                BindTouch(swatch, collectionPanel, "OnColor" + i);
            }

            var colStatus = CreateText(pageMyDarts.transform, "CollectionStatus",
                "Loading...", 24, TextAlignmentOptions.Left);
            SetTopRect(colStatus.rectTransform, 866, 44);

            var equipButton = CreateButton(pageMyDarts.transform, "EquipButton", "EQUIP");
            SetBottomHalfRect(equipButton.GetComponent<RectTransform>(), 20, 82, true);
            StyleButton(equipButton, true);
            BindTouch(equipButton, collectionPanel, "OnClickEquip");

            var resetButton = CreateButton(pageMyDarts.transform, "ResetButton", "RESET");
            SetBottomHalfRect(resetButton.GetComponent<RectTransform>(), 20, 82, false);
            StyleButton(resetButton, false);
            BindTouch(resetButton, collectionPanel, "OnClickReset");

            // ============================================================
            //  ⑦ クレジット
            // ============================================================
            var pageCredit = CreatePage(pagesRoot.transform, "Page6_Credit");

            var creditTitle = CreateText(pageCredit.transform, "CreditTitle", "CREDIT", 46, TextAlignmentOptions.Left);
            creditTitle.color = ColGlow;
            SetTopRect(creditTitle.rectTransform, 30, 62);

            var creditBig = CreateText(pageCredit.transform, "CreditBig", "-", 130, TextAlignmentOptions.Center);
            creditBig.color = ColWhite;
            SetTopRect(creditBig.rectTransform, 300, 190);

            var creditNote = CreateText(pageCredit.transform, "CreditNote",
                "Credits are used to pull the gacha.", 26, TextAlignmentOptions.Center);
            creditNote.color = ColSub;
            SetTopRect(creditNote.rectTransform, 510, 50);

            // ============================================================
            //  ガチャ演出（動画＋パーツ）
            //
            //  ・隠し撮影セット … 地面のはるか下に置く。人からは見えない
            //  ・カメラ         … そこを撮ってレンダーテクスチャへ。使う時だけ動かす
            //  ・動画           … 別のレンダーテクスチャへ。画面の背景として貼る
            //
            //  重ねる順番は「動画が下、パーツが上」。
            //  透過が効かなかった場合はここの順番と背景を見直す。
            // ============================================================
            // ---- 隠し撮影セット ----
            var studio = NewChild(root.transform, "Studio");
            studio.transform.position = _settings.playerPanelPosition + new Vector3(0f, -1000f, 0f);

            var camGo = NewChild(studio.transform, "StudioCamera");
            camGo.transform.localPosition = new Vector3(0f, 0f, -1.2f);

            var studioCam = camGo.AddComponent<Camera>();
            studioCam.orthographic = true;
            studioCam.orthographicSize = 0.45f;
            studioCam.nearClipPlane = 0.05f;
            studioCam.farClipPlane = 4f;
            studioCam.clearFlags = CameraClearFlags.SolidColor;
            studioCam.backgroundColor = new Color(0f, 0f, 0f, 0f);  // 透明で塗る
            studioCam.allowMSAA = false;
            studioCam.allowHDR = false;
            studioCam.useOcclusionCulling = false;
            studioCam.targetTexture = studioRt;
            studioCam.enabled = false;                              // 使う時だけ動かす

            // ---- 動画プレイヤー ----
            var videoGo = NewChild(root.transform, "VideoPlayer");
            videoGo.transform.position = _settings.playerPanelPosition;

            var unityVideo = videoGo.AddComponent<UnityEngine.Video.VideoPlayer>();
            unityVideo.playOnAwake = false;
            unityVideo.source = UnityEngine.Video.VideoSource.Url;
            unityVideo.renderMode = UnityEngine.Video.VideoRenderMode.RenderTexture;
            unityVideo.targetTexture = videoRt;
            unityVideo.audioOutputMode = UnityEngine.Video.VideoAudioOutputMode.AudioSource;
            unityVideo.skipOnDrop = true;

            var videoAudio = videoGo.AddComponent<AudioSource>();
            videoAudio.playOnAwake = false;
            videoAudio.spatialBlend = 1f;
            videoAudio.minDistance = 2f;
            videoAudio.maxDistance = 14f;
            unityVideo.SetTargetAudioSource(0, videoAudio);

            var vrcVideo = videoGo.AddComponent<VRC.SDK3.Video.Components.VRCUnityVideoPlayer>();
            var videoBehaviour = videoGo.AddUdonSharpComponent<MyDartsVideo>();

            // ---- 演出の画面（帯の下を丸ごと覆う）----
            var stagePanel = NewChild(frame.transform, "GachaStage");
            var stagePanelRt = stagePanel.AddComponent<RectTransform>();
            SetAnchors(stagePanelRt, Vector2.zero, Vector2.one, Vector2.zero, new Vector2(0f, -HeaderH));

            var stageBg = stagePanel.AddComponent<Image>();
            stageBg.color = ColBg;
            stageBg.raycastTarget = false;

            var videoImage = CreateRawImage(stagePanel.transform, "VideoImage", videoRt);
            SetAnchors(videoImage.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            // パーツはまん中に。動画側で空けてもらっている場所に合わせる
            var studioImage = CreateRawImage(stagePanel.transform, "StudioImage", studioRt);
            SetAnchors(studioImage.rectTransform,
                       new Vector2(0.2f, 0.30f), new Vector2(0.8f, 0.75f),
                       Vector2.zero, Vector2.zero);

            // 画面のどこを触ってもタップ扱いにする
            var tapArea = CreateButton(stagePanel.transform, "TapArea", "");
            SetAnchors(tapArea.GetComponent<RectTransform>(), Vector2.zero, Vector2.one,
                       Vector2.zero, Vector2.zero);
            tapArea.targetGraphic.color = new Color(1f, 1f, 1f, 0f);

            var tapHint = CreateText(stagePanel.transform, "TapHint", "TAP!", 72, TextAlignmentOptions.Center);
            tapHint.color = ColWhite;
            SetAnchors(tapHint.rectTransform, new Vector2(0f, 0.10f), new Vector2(1f, 0.22f),
                       Vector2.zero, Vector2.zero);

            var resultLine = CreateText(stagePanel.transform, "StageResultLine", "", 54, TextAlignmentOptions.Center);
            SetAnchors(resultLine.rectTransform, new Vector2(0f, 0.16f), new Vector2(1f, 0.26f),
                       Vector2.zero, Vector2.zero);

            var resultTier = CreateText(stagePanel.transform, "StageResultTier", "", 34, TextAlignmentOptions.Center);
            SetAnchors(resultTier.rectTransform, new Vector2(0f, 0.10f), new Vector2(1f, 0.16f),
                       Vector2.zero, Vector2.zero);

            var newBadgeText = CreateText(stagePanel.transform, "NewBadge", "NEW!", 40, TextAlignmentOptions.Center);
            newBadgeText.color = ColGlow;
            SetAnchors(newBadgeText.rectTransform, new Vector2(0f, 0.76f), new Vector2(1f, 0.84f),
                       Vector2.zero, Vector2.zero);

            var stageClose = CreateButton(stagePanel.transform, "StageCloseButton", "CLOSE");
            SetAnchors(stageClose.GetComponent<RectTransform>(),
                       new Vector2(0.28f, 0.015f), new Vector2(0.72f, 0.085f),
                       Vector2.zero, Vector2.zero);
            StyleButton(stageClose, false);

            stagePanel.SetActive(false);

            // ---- 動画が使えない人への案内 ----
            var videoNotice = NewChild(frame.transform, "VideoNotice");
            var noticeRt = videoNotice.AddComponent<RectTransform>();
            SetAnchors(noticeRt, new Vector2(0.06f, 0.30f), new Vector2(0.94f, 0.62f),
                       Vector2.zero, Vector2.zero);

            var noticeBg = videoNotice.AddComponent<Image>();
            noticeBg.color = ColPanel;

            var noticeTitle = CreateText(videoNotice.transform, "NoticeTitle",
                "VIDEO IS BLOCKED", 40, TextAlignmentOptions.Center);
            noticeTitle.color = ColGlow;
            SetTopRect(noticeTitle.rectTransform, 24, 56);

            var noticeBody = CreateText(videoNotice.transform, "NoticeBody",
                "Turn on \"Untrusted URLs\" in your VRChat settings\nto see the gacha animation.\n\nYou can still pull without it.",
                26, TextAlignmentOptions.Center);
            noticeBody.color = ColText;
            SetTopRect(noticeBody.rectTransform, 92, 160);

            var noticeClose = CreateButton(videoNotice.transform, "NoticeCloseButton", "OK");
            SetBottomHalfRect(noticeClose.GetComponent<RectTransform>(), 18, 64, true, 60f, 0f);
            SetAnchors(noticeClose.GetComponent<RectTransform>(),
                       new Vector2(0.3f, 0f), new Vector2(0.7f, 0f),
                       new Vector2(0f, 18f), new Vector2(0f, 82f));
            StyleButton(noticeClose, false);

            videoNotice.SetActive(false);

            // ---- 演出まわりの配線 ----
            BindTouch(tapArea, stage, "OnTap");
            BindTouch(stageClose, stage, "OnClose");
            BindTouch(noticeClose, videoBehaviour, "HideNotice");

            videoBack.texture = videoRt;

            WireVideo(videoBehaviour, vrcVideo, videoNotice, stagePanel,
                      videoBack.gameObject, cabinetCanvas.transform, debug);
            PushVideoUrl(videoBehaviour);

            WireStage(stage, sender, fetcher, videoBehaviour, screens, debug,
                      stagePanel, studioCam, tapHint.gameObject,
                      resultLine, resultTier, newBadgeText.gameObject,
                      stageClose.gameObject);

            // ---------- 画面の並び（MyDartsScreens の番号と同じ順） ----------
            var pageObjects = new GameObject[]
            {
                pageTop, pageMenu, pageGacha, pageGachaNormal,
                pageGachaPickup, pageMyDarts, pageCredit,
            };

            // 最初はトップだけ出しておく
            for (var i = 1; i < pageObjects.Length; i++) pageObjects[i].SetActive(false);

            // ---------- 画面まわりの参照とボタン ----------
            WireScreens(screens, pageObjects, playButton.gameObject, registerGroup,
                        topHint, fetcher, sender, debug, creditText, creditBig, touchInput);

            BindTouch(playButton, screens, "GoMenu");
            BindTouch(menuButtons[0], screens, "GoGacha");
            BindTouch(menuButtons[1], screens, "GoMyDarts");
            BindTouch(menuButtons[2], screens, "GoCredit");
            BindTouch(normalButton, screens, "GoGachaNormal");
            BindTouch(pickupButton, screens, "GoGachaPickup");
            BindTouch(backButton, screens, "GoBack");
            BindTouch(homeButton, screens, "GoHome");

            WireGachaPanel(gachaPanel, sender, fetcher,
                           null, priceOne, priceSet, resultTitle, resultBody, collectionText);

            WireCollectionPanel(collectionPanel, sender, fetcher, debug,
                                countTexts, listTexts, selectTexts, colStatus);
            WireCollectionColors(collectionPanel, colorPresets, colorText, colorSwatches);

            // ---------- 参照を繋ぐ ----------
            Wire(register, status, debug, urlOutput, urlInput, registerGroup, fetcher, sender);
            WireTester(tester, sender, fetcher, debug, partFields, currentText, priceLabels, cartText);
            WireFetcher(fetcher, status, debug, registerGroup);
            WireBridge(bridge, debug);

            var allLocks = new List<Button>();
            allLocks.Add(addButton);
            allLocks.Add(subButton);
            allLocks.AddRange(partButtons);
            allLocks.Add(buyButton);
            allLocks.Add(gachaOne);
            allLocks.Add(gachaSet);
            // EQUIP はロックしない。押した瞬間に反映して裏で保存する作りなので、
            // 送信中でも押せてよい（積み直されて最後の1つだけが送られる）
            WireSender(sender, fetcher, status, debug, allLocks.ToArray());

            WireStatus(status, title, body);
            WireDebug(debug, log, state);
            PushSettingsTo(register);

            // ---------- ボタンのイベント ----------
            BindTouch(buildButton, register, "OnClickBuildUrl");
            BindTouch(sendButton, register, "OnSubmitUrl");
            BindUrlInput(urlInput, register, "OnSubmitUrl");
            BindButton(addButton, sender, "OnClickAddCoin");
            BindButton(subButton, sender, "OnClickSubCoin");

            // ---------- 指で押せるようにする ----------
            // ここまでに集めたボタン全部に、指用の受け口を付ける。
            // 相手も合図もポインタ用と同じものを入れるので、
            // どちらで押しても起きることは変わらない。
            var touchButtons = BuildTouchButtons();

            WireTouchInput(touchInput, touchButtons, cabinetCanvas.transform,
                           cabinetCanvas.GetComponent<GraphicRaycaster>(), debug);

            EnsureEventSystem(root.transform);

            if (_settings.debugPanelHiddenByDefault)
            {
                debugCanvas.SetActive(false);
            }

            Selection.activeGameObject = root;
            EditorUtility.SetDirty(root);
            Debug.Log("[MyDarts] マイダーツ拡張を生成しました。");
        }

        /// <summary>
        /// 画面1枚ぶんの入れ物。中身は親いっぱいに広げる。
        /// 背景は付けない（筐体の枠が透けて見えるように）。
        /// </summary>
        private static GameObject CreatePage(Transform parent, string name)
        {
            var go = NewChild(parent, name);
            var rt = go.AddComponent<RectTransform>();
            SetAnchors(rt, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
            return go;
        }

        /// <summary>
        /// DefaultColor の初期候補。
        /// ダーツギミック側の色設定と同じ場所に書き込むので、
        /// あちらで選べる色に近いものを並べてある。
        /// </summary>
        private static Color32[] DefaultColorPresets()
        {
            return new Color32[]
            {
                new Color32(240, 240, 240, 255), // White
                new Color32( 40,  40,  44, 255), // Black
                new Color32(220,  60,  60, 255), // Red
                new Color32(235, 140,  40, 255), // Orange
                new Color32(235, 210,  60, 255), // Yellow
                new Color32( 70, 190, 110, 255), // Green
                new Color32( 60, 140, 230, 255), // Blue
                new Color32(170,  90, 220, 255), // Purple
            };
        }


        // ============================================================
        //  参照だけ繋ぎ直す
        // ============================================================
        /// <summary>まとめ画面から呼ぶ。根本を探して繋ぎ直す</summary>
        public void RelinkAll() { Relink(FindRoot()); }

        private void Relink(GameObject root)
        {
            if (root == null) return;

            var register = root.GetComponentInChildren<MyDartsRegister>(true);
            var status = root.GetComponentInChildren<MyDartsStatusPanel>(true);
            var debug = root.GetComponentInChildren<MyDartsDebugPanel>(true);

            if (register == null)
            {
                Debug.LogError("[MyDarts] MyDartsRegister が見つかりません。作り直してください。");
                return;
            }

            var urlOutput = FindByName<TMP_InputField>(root.transform, "UrlOutput");
            var urlInput = root.GetComponentInChildren<VRCUrlInputField>(true);

            var relinkGroup = FindChildByName(root.transform, "RegisterGroup");
            Wire(register, status, debug, urlOutput, urlInput, relinkGroup,
                 root.GetComponentInChildren<MyDartsFetcher>(true),
                 root.GetComponentInChildren<MyDartsSender>(true));

            var bridge = root.GetComponentInChildren<MyDartsCrecentBridge>(true);
            if (bridge != null) WireBridge(bridge, debug);

            var fetcher = root.GetComponentInChildren<MyDartsFetcher>(true);
            if (fetcher != null)
            {
                WireFetcher(fetcher, status, debug, relinkGroup);
            }
            else
            {
                Debug.LogWarning("[MyDarts] MyDartsFetcher が見つかりません。読み込みを使うには作り直してください。");
            }

            var sender = root.GetComponentInChildren<MyDartsSender>(true);
            if (sender != null)
            {
                var locks = new List<Button>();

                var gachaOne = FindByName<Button>(root.transform, "GachaOneButton");
                if (gachaOne != null)
                {
                    BindButton(gachaOne, sender, "OnClickGachaOne");
                    locks.Add(gachaOne);
                }

                var gachaSet = FindByName<Button>(root.transform, "GachaSetButton");
                if (gachaSet != null)
                {
                    BindButton(gachaSet, sender, "OnClickGachaSet");
                    locks.Add(gachaSet);
                }

                var gachaPanel = root.GetComponentInChildren<MyDartsGachaPanel>(true);
                if (gachaPanel != null)
                {
                    WireGachaPanel(gachaPanel, sender, fetcher,
                        FindByName<TextMeshProUGUI>(root.transform, "Coins"),
                        FindByName<TextMeshProUGUI>(root.transform, "PriceOne"),
                        FindByName<TextMeshProUGUI>(root.transform, "PriceSet"),
                        FindByName<TextMeshProUGUI>(root.transform, "ResultTitle"),
                        FindByName<TextMeshProUGUI>(root.transform, "ResultBody"),
                        FindByName<TextMeshProUGUI>(root.transform, "Collection"));
                }

                // ---- コレクション画面 ----
                var collectionPanel = root.GetComponentInChildren<MyDartsCollectionPanel>(true);
                if (collectionPanel != null)
                {
                    var colNames = new string[] { "Tip", "Barrel", "Shaft", "Flight" };
                    var colPrevEvents = new string[] { "OnPrevTip", "OnPrevBarrel", "OnPrevShaft", "OnPrevFlight" };
                    var colNextEvents = new string[] { "OnNextTip", "OnNextBarrel", "OnNextShaft", "OnNextFlight" };

                    var countTexts = new TextMeshProUGUI[4];
                    var listTexts = new TextMeshProUGUI[4];
                    var selectTexts = new TextMeshProUGUI[4];

                    for (var i = 0; i < 4; i++)
                    {
                        countTexts[i] = FindByName<TextMeshProUGUI>(root.transform, colNames[i] + "Count");
                        listTexts[i] = FindByName<TextMeshProUGUI>(root.transform, colNames[i] + "Owned");
                        selectTexts[i] = FindByName<TextMeshProUGUI>(root.transform, colNames[i] + "Sel");

                        var prev = FindByName<Button>(root.transform, colNames[i] + "Prev");
                        if (prev != null) BindButton(prev, collectionPanel, colPrevEvents[i]);

                        var next = FindByName<Button>(root.transform, colNames[i] + "Next");
                        if (next != null) BindButton(next, collectionPanel, colNextEvents[i]);
                    }

                    // EQUIP はロックしない（送信中でも押せてよい）
                    var equipBtn = FindByName<Button>(root.transform, "EquipButton");
                    if (equipBtn != null) BindButton(equipBtn, collectionPanel, "OnClickEquip");

                    var resetBtn = FindByName<Button>(root.transform, "ResetButton");
                    if (resetBtn != null) BindButton(resetBtn, collectionPanel, "OnClickReset");

                    WireCollectionPanel(collectionPanel, sender, fetcher, debug,
                        countTexts, listTexts, selectTexts,
                        FindByName<TextMeshProUGUI>(root.transform, "CollectionStatus"));

                    // ---- DefaultColor ----
                    var presets = DefaultColorPresets();
                    var swatches = new Image[presets.Length];
                    for (var i = 0; i < presets.Length; i++)
                    {
                        var sw = FindByName<Button>(root.transform, "Color" + i);
                        if (sw == null) continue;

                        BindButton(sw, collectionPanel, "OnColor" + i);
                        swatches[i] = sw.targetGraphic as Image;
                    }

                    WireCollectionColors(collectionPanel, presets,
                        FindByName<TextMeshProUGUI>(root.transform, "ColorText"), swatches);
                }

                var addButton = FindByName<Button>(root.transform, "TestAddCoin");
                var subButton = FindByName<Button>(root.transform, "TestSubCoin");
                locks.Add(addButton);
                locks.Add(subButton);

                if (addButton != null) BindButton(addButton, sender, "OnClickAddCoin");
                if (subButton != null) BindButton(subButton, sender, "OnClickSubCoin");

                // ---- テスト用パーツパネル ----
                var tester = root.GetComponentInChildren<MyDartsPartsTester>(true);
                if (tester != null)
                {
                    var partNames = new string[] { "Tip", "Barrel", "Shaft", "Flight", "Shape" };
                    var partEvents = new string[] { "OnClickTip", "OnClickBarrel", "OnClickShaft", "OnClickFlight", "OnClickShape" };
                    var fields = new TMP_InputField[5];

                    for (var i = 0; i < partNames.Length; i++)
                    {
                        fields[i] = FindByName<TMP_InputField>(root.transform, partNames[i] + "Field");

                        var btn = FindByName<Button>(root.transform, partNames[i] + "Button");
                        if (btn != null)
                        {
                            BindButton(btn, tester, partEvents[i]);
                            locks.Add(btn);
                        }
                    }

                    var labels = new TextMeshProUGUI[5];
                    for (var i = 0; i < partNames.Length; i++)
                    {
                        labels[i] = FindByName<TextMeshProUGUI>(root.transform, partNames[i] + "Price");
                    }

                    var buyBtn = FindByName<Button>(root.transform, "BuyButton");
                    if (buyBtn != null)
                    {
                        BindButton(buyBtn, tester, "OnClickBuy");
                        locks.Add(buyBtn);
                    }

                    var clearBtn = FindByName<Button>(root.transform, "ClearButton");
                    if (clearBtn != null) BindButton(clearBtn, tester, "OnClickClear");

                    WireTester(tester, sender, fetcher, debug, fields,
                               FindByName<TextMeshProUGUI>(root.transform, "Current"), labels,
                               FindByName<TextMeshProUGUI>(root.transform, "Cart"));
                }

                WireSender(sender, fetcher, status, debug, locks.ToArray());
            }
            else
            {
                Debug.LogWarning("[MyDarts] MyDartsSender が見つかりません。送信を使うには作り直してください。");
            }

            if (status != null)
            {
                WireStatus(status,
                    FindByName<TextMeshProUGUI>(root.transform, "Title"),
                    FindByName<TextMeshProUGUI>(root.transform, "Body"));
            }

            if (debug != null)
            {
                WireDebug(debug,
                    FindByName<TextMeshProUGUI>(root.transform, "Log"),
                    FindByName<TextMeshProUGUI>(root.transform, "State"));
            }

            // ---- 筐体の画面切り替え ----
            var screens = root.GetComponentInChildren<MyDartsScreens>(true);
            if (screens != null)
            {
                var pageNames = new string[]
                {
                    "Page0_Top", "Page1_Menu", "Page2_Gacha", "Page3_GachaNormal",
                    "Page4_GachaPickup", "Page5_MyDarts", "Page6_Credit",
                };

                var pages = new GameObject[pageNames.Length];
                for (var i = 0; i < pageNames.Length; i++)
                {
                    pages[i] = FindChildByName(root.transform, pageNames[i]);
                }

                var playBtn = FindByName<Button>(root.transform, "PlayButton");

                WireScreens(screens, pages,
                    playBtn != null ? playBtn.gameObject : null,
                    FindChildByName(root.transform, "RegisterGroup"),
                    FindByName<TextMeshProUGUI>(root.transform, "TopHint"),
                    fetcher, sender, debug,
                    FindByName<TextMeshProUGUI>(root.transform, "CreditText"),
                    FindByName<TextMeshProUGUI>(root.transform, "CreditBig"),
                    root.GetComponentInChildren<MyDartsTouchInput>(true));

                BindScreenButton(root, "PlayButton", screens, "GoMenu");
                BindScreenButton(root, "Menu0Button", screens, "GoGacha");
                BindScreenButton(root, "Menu1Button", screens, "GoMyDarts");
                BindScreenButton(root, "Menu2Button", screens, "GoCredit");
                BindScreenButton(root, "GachaNormalButton", screens, "GoGachaNormal");
                BindScreenButton(root, "GachaPickupButton", screens, "GoGachaPickup");
                BindScreenButton(root, "BackButton", screens, "GoBack");
                BindScreenButton(root, "HomeButton", screens, "GoHome");
            }

            // ---- はみ出しの切り落とし ----
            var pagesGo = FindChildByName(root.transform, "Pages");
            if (pagesGo != null && pagesGo.GetComponent<RectMask2D>() == null)
            {
                pagesGo.AddComponent<RectMask2D>();
                Debug.Log("[MyDarts] 画面のはみ出しを隠す切り抜きを足しました。");
            }

            // ---- 指で押す仕掛け ----
            var touchInput = root.GetComponentInChildren<MyDartsTouchInput>(true);
            if (touchInput != null)
            {
                var cabinet = FindChildByName(root.transform, "CabinetCanvas");

                WireTouchInput(touchInput,
                    root.GetComponentsInChildren<MyDartsTouchButton>(true),
                    cabinet != null ? cabinet.transform : null,
                    cabinet != null ? cabinet.GetComponent<GraphicRaycaster>() : null,
                    debug);
            }

            PushSettingsTo(register);

            var buildButton = FindByName<Button>(root.transform, "BuildUrlButton");
            if (buildButton != null) BindButton(buildButton, register, "OnClickBuildUrl");

            var sendButton = FindByName<Button>(root.transform, "SendButton");
            if (sendButton != null) BindButton(sendButton, register, "OnSubmitUrl");

            if (urlInput != null) BindUrlInput(urlInput, register, "OnSubmitUrl");

            EditorUtility.SetDirty(root);
            Debug.Log("[MyDarts] 参照を繋ぎ直しました。");
        }

        private void PushSettings(GameObject root)
        {
            if (root == null) return;
            var register = root.GetComponentInChildren<MyDartsRegister>(true);
            if (register != null) PushSettingsTo(register);

            var fetcher = root.GetComponentInChildren<MyDartsFetcher>(true);
            if (fetcher != null) PushLedgerUrl(fetcher);

            var sender = root.GetComponentInChildren<MyDartsSender>(true);
            if (sender != null) PushSenderUrls(sender);

            Debug.Log("[MyDarts] 設定値を流し込みました。");
        }

        // ============================================================
        //  参照の書き込み（SerializedObject経由 → Udonへ反映）
        // ============================================================
        private static void Wire(MyDartsRegister register,
                                 MyDartsStatusPanel status,
                                 MyDartsDebugPanel debug,
                                 TMP_InputField urlOutput,
                                 VRCUrlInputField urlInput,
                                 GameObject registerGroup,
                                 MyDartsFetcher fetcher = null,
                                 MyDartsSender sender = null)
        {
            if (register == null) return;
            register.UpdateProxy();

            var so = new SerializedObject(register);
            SetObj(so, "statusPanel", status);
            SetObj(so, "debugPanel", debug);
            SetObj(so, "urlOutputField", urlOutput);
            SetObj(so, "urlInputField", urlInput);
            SetObj(so, "registerGroup", registerGroup);

            // 登録が通ったことを読み込み側と送信側へ伝えるために要る
            SetObj(so, "fetcher", fetcher);
            SetObj(so, "sender", sender);

            so.ApplyModifiedPropertiesWithoutUndo();

            register.ApplyProxyModifications();
        }

        /// <summary>
        /// ブリッジはダーツギミック本体を参照しない（キー名で読む）ので、
        /// 繋ぐのはデバッグパネルだけでよい。
        /// </summary>
        private static void WireBridge(MyDartsCrecentBridge bridge, MyDartsDebugPanel debug)
        {
            if (bridge == null) return;
            bridge.UpdateProxy();

            var so = new SerializedObject(bridge);
            SetObj(so, "debugPanel", debug);
            so.ApplyModifiedPropertiesWithoutUndo();

            bridge.ApplyProxyModifications();
        }

        /// <summary>ガチャ画面の参照を繋ぐ</summary>
        private static void WireGachaPanel(MyDartsGachaPanel panel,
                                           MyDartsSender sender,
                                           MyDartsFetcher fetcher,
                                           TextMeshProUGUI coin,
                                           TextMeshProUGUI priceOne,
                                           TextMeshProUGUI priceSet,
                                           TextMeshProUGUI resultTitle,
                                           TextMeshProUGUI resultBody,
                                           TextMeshProUGUI collection)
        {
            if (panel == null) return;
            panel.UpdateProxy();

            var so = new SerializedObject(panel);
            SetObj(so, "sender", sender);
            SetObj(so, "fetcher", fetcher);
            SetObj(so, "coinText", coin);
            SetObj(so, "priceOneText", priceOne);
            SetObj(so, "priceSetText", priceSet);
            SetObj(so, "resultTitle", resultTitle);
            SetObj(so, "resultBody", resultBody);
            SetObj(so, "collectionText", collection);
            so.ApplyModifiedPropertiesWithoutUndo();

            panel.ApplyProxyModifications();
        }

        /// <summary>コレクション画面の参照を繋ぐ</summary>
        private static void WireCollectionPanel(MyDartsCollectionPanel panel,
                                                MyDartsSender sender,
                                                MyDartsFetcher fetcher,
                                                MyDartsDebugPanel debug,
                                                TextMeshProUGUI[] countTexts,
                                                TextMeshProUGUI[] listTexts,
                                                TextMeshProUGUI[] selectTexts,
                                                TextMeshProUGUI statusText)
        {
            if (panel == null) return;
            panel.UpdateProxy();

            var so = new SerializedObject(panel);
            SetObj(so, "sender", sender);
            SetObj(so, "fetcher", fetcher);
            SetObj(so, "debugPanel", debug);
            SetObj(so, "statusText", statusText);
            so.ApplyModifiedPropertiesWithoutUndo();

            panel.ApplyProxyModifications();

            PushArrayField(panel, "countTexts", countTexts);
            PushArrayField(panel, "listTexts", listTexts);
            PushArrayField(panel, "selectTexts", selectTexts);
        }

        /// <summary>
        /// DefaultColor まわりを繋ぐ。
        /// 色そのものはダーツギミック側の保存場所を読み書きするので、
        /// ここで入れるのは「候補の色」と「見た目」だけ。
        /// </summary>
        private static void WireCollectionColors(MyDartsCollectionPanel panel,
                                                 Color32[] presets,
                                                 TextMeshProUGUI colorText,
                                                 Image[] swatches)
        {
            if (panel == null) return;
            panel.UpdateProxy();

            var so = new SerializedObject(panel);
            SetObj(so, "colorText", colorText);
            so.ApplyModifiedPropertiesWithoutUndo();

            panel.ApplyProxyModifications();

            PushArrayField(panel, "colorPresets", presets);
            PushArrayField(panel, "colorSwatches", swatches);
        }


        /// <summary>
        /// 映し先のレンダーテクスチャを用意する。
        /// 既にあって大きさも同じならそれを使う。作り直すと参照が切れるため。
        /// </summary>
        private static RenderTexture EnsureRenderTexture(string path, int width, int height)
        {
            EnsureSettingsDir();

            var rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);
            if (rt != null && rt.width == width && rt.height == height) return rt;

            if (rt != null) AssetDatabase.DeleteAsset(path);

            rt = new RenderTexture(width, height, 16, RenderTextureFormat.ARGB32);
            rt.name = Path.GetFileNameWithoutExtension(path);
            rt.antiAliasing = 1;
            rt.useMipMap = false;
            rt.filterMode = FilterMode.Bilinear;
            rt.wrapMode = TextureWrapMode.Clamp;

            AssetDatabase.CreateAsset(rt, path);
            AssetDatabase.SaveAssets();

            return rt;
        }

        /// <summary>
        /// 動画の黒いところを抜くためのマテリアルを用意しておく。
        /// 使いたい板の Material に差し替えれば効く。既定では使わない。
        /// </summary>
        private static void EnsureAlphaMaterial()
        {
            EnsureSettingsDir();

            var path = SettingsDir + "/MyDartsVideoAlpha.mat";
            if (AssetDatabase.LoadAssetAtPath<Material>(path) != null) return;

            var shader = Shader.Find("MyDarts/UI Video Alpha");
            if (shader == null) return;

            var mat = new Material(shader);
            AssetDatabase.CreateAsset(mat, path);
            AssetDatabase.SaveAssets();

            Debug.Log("[MyDarts] 黒抜き用のマテリアルを用意しました: " + path);
        }

        private static void EnsureSettingsDir()
        {
            if (AssetDatabase.IsValidFolder(SettingsDir)) return;

            var name = SettingsDir.Substring("Assets/".Length);
            AssetDatabase.CreateFolder("Assets", name);
        }

        private static RawImage CreateRawImage(Transform parent, string name, Texture texture)
        {
            var go = NewChild(parent, name);
            go.AddComponent<RectTransform>();

            var img = go.AddComponent<RawImage>();
            img.texture = texture;
            img.raycastTarget = false;

            return img;
        }

        /// <summary>動画まわりの参照を繋ぐ</summary>
        private static void WireVideo(MyDartsVideo video,
                                      Component player,
                                      GameObject blockedNotice,
                                      GameObject screenObject,
                                      GameObject backgroundScreen,
                                      Transform cabinet,
                                      MyDartsDebugPanel debug)
        {
            if (video == null) return;
            video.UpdateProxy();

            var so = new SerializedObject(video);
            SetObj(so, "player", player);
            SetObj(so, "blockedNotice", blockedNotice);
            SetObj(so, "screenObject", screenObject);
            SetObj(so, "backgroundScreen", backgroundScreen);
            SetObj(so, "cabinet", cabinet);
            SetObj(so, "debugPanel", debug);
            so.ApplyModifiedPropertiesWithoutUndo();

            video.ApplyProxyModifications();
        }

        /// <summary>
        /// 動画のURLを焼き込む。
        /// VRCUrl は実行中に組み立てられないので、ここで入れておく必要がある。
        /// </summary>
        private void PushVideoUrl(MyDartsVideo video)
        {
            if (video == null || _settings == null) return;

            var url = _settings.gachaVideoUrl;
            if (string.IsNullOrEmpty(url)) return;

            var f = typeof(MyDartsVideo).GetField(
                "videoUrl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return;

            video.UpdateProxy();
            f.SetValue(video, new VRCUrl(url));
            video.ApplyProxyModifications();
            EditorUtility.SetDirty(video);
        }

        /// <summary>ガチャ演出の進行役に参照を繋ぐ</summary>
        private static void WireStage(MyDartsGachaStage stage,
                                      MyDartsSender sender,
                                      MyDartsFetcher fetcher,
                                      MyDartsVideo video,
                                      MyDartsScreens screens,
                                      MyDartsDebugPanel debug,
                                      GameObject stageGroup,
                                      Camera studioCamera,
                                      GameObject tapHint,
                                      TextMeshProUGUI resultLine,
                                      TextMeshProUGUI resultTier,
                                      GameObject newBadge,
                                      GameObject closeButton)
        {
            if (stage == null) return;
            stage.UpdateProxy();

            var so = new SerializedObject(stage);
            SetObj(so, "sender", sender);
            SetObj(so, "fetcher", fetcher);
            SetObj(so, "video", video);
            SetObj(so, "screens", screens);
            SetObj(so, "debugPanel", debug);
            SetObj(so, "stageGroup", stageGroup);
            SetObj(so, "studioCamera", studioCamera);
            SetObj(so, "tapHint", tapHint);
            SetObj(so, "resultLine", resultLine);
            SetObj(so, "resultTier", resultTier);
            SetObj(so, "newBadge", newBadge);
            SetObj(so, "closeButton", closeButton);
            so.ApplyModifiedPropertiesWithoutUndo();

            stage.ApplyProxyModifications();
        }

        /// <summary>筐体の画面切り替えに参照を繋ぐ</summary>
        private static void WireScreens(MyDartsScreens screens,
                                        GameObject[] pages,
                                        GameObject playButton,
                                        GameObject registerGroup,
                                        TextMeshProUGUI topHint,
                                        MyDartsFetcher fetcher,
                                        MyDartsSender sender,
                                        MyDartsDebugPanel debug,
                                        TextMeshProUGUI creditText,
                                        TextMeshProUGUI creditBig,
                                        MyDartsTouchInput touchInput)
        {
            if (screens == null) return;
            screens.UpdateProxy();

            var so = new SerializedObject(screens);
            SetObj(so, "playButton", playButton);
            SetObj(so, "registerGroup", registerGroup);
            SetObj(so, "topHint", topHint);
            SetObj(so, "fetcher", fetcher);
            SetObj(so, "sender", sender);
            SetObj(so, "debugPanel", debug);
            SetObj(so, "creditText", creditText);
            SetObj(so, "creditBig", creditBig);
            SetObj(so, "touchInput", touchInput);
            so.ApplyModifiedPropertiesWithoutUndo();

            screens.ApplyProxyModifications();

            PushArrayField(screens, "pages", pages);
        }

        /// <summary>
        /// 集めておいたボタンに、指用の受け口を付けて回る。
        ///
        /// 当たり判定はボタン自身の枠を使うので、コライダーは足さない。
        /// ダーツや人の動きに余計なものを増やさないため。
        /// </summary>
        private MyDartsTouchButton[] BuildTouchButtons()
        {
            var list = new List<MyDartsTouchButton>();

            for (var i = 0; i < _touchButtons.Count; i++)
            {
                var button = _touchButtons[i];
                if (button == null) continue;

                var go = button.gameObject;

                var touch = go.GetComponent<MyDartsTouchButton>();
                if (touch == null) touch = go.AddUdonSharpComponent<MyDartsTouchButton>();

                touch.UpdateProxy();

                var so = new SerializedObject(touch);
                SetObj(so, "target", _touchTargets[i]);
                SetStr(so, "eventName", _touchEvents[i]);
                SetObj(so, "graphic", button.targetGraphic as Image);
                SetObj(so, "body", go.GetComponent<RectTransform>());
                so.ApplyModifiedPropertiesWithoutUndo();

                touch.ApplyProxyModifications();

                list.Add(touch);
            }

            return list.ToArray();
        }

        /// <summary>指で押す仕掛けに参照を繋ぐ</summary>
        private static void WireTouchInput(MyDartsTouchInput input,
                                           MyDartsTouchButton[] buttons,
                                           Transform cabinet,
                                           GraphicRaycaster raycaster,
                                           MyDartsDebugPanel debug)
        {
            if (input == null) return;
            input.UpdateProxy();

            var so = new SerializedObject(input);
            SetObj(so, "cabinet", cabinet);
            SetObj(so, "raycaster", raycaster);
            SetObj(so, "debugPanel", debug);
            so.ApplyModifiedPropertiesWithoutUndo();

            input.ApplyProxyModifications();

            PushArrayField(input, "buttons", buttons);
        }

        /// <summary>
        /// 配列のフィールドを入れる。
        /// SerializedProperty で配列を組むより、直接入れてから
        /// Udon へ反映したほうが確実なのでこの形にしてある。
        /// </summary>
        private static void PushArrayField(UdonSharpBehaviour target, string fieldName, object value)
        {
            if (target == null) return;

            var f = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return;

            target.UpdateProxy();
            f.SetValue(target, value);
            target.ApplyProxyModifications();
            EditorUtility.SetDirty(target);
        }

        /// <summary>テスト用パーツパネルの参照を繋ぐ</summary>
        private static void WireTester(MyDartsPartsTester tester,
                                       MyDartsSender sender,
                                       MyDartsFetcher fetcher,
                                       MyDartsDebugPanel debug,
                                       TMP_InputField[] fields,
                                       TextMeshProUGUI currentText,
                                       TextMeshProUGUI[] priceLabels,
                                       TextMeshProUGUI cartText)
        {
            if (tester == null) return;
            tester.UpdateProxy();

            var so = new SerializedObject(tester);
            SetObj(so, "sender", sender);
            SetObj(so, "fetcher", fetcher);
            SetObj(so, "debugPanel", debug);
            SetObj(so, "currentText", currentText);
            SetObj(so, "cartText", cartText);

            if (fields != null && fields.Length >= 5)
            {
                SetObj(so, "tipField", fields[0]);
                SetObj(so, "barrelField", fields[1]);
                SetObj(so, "shaftField", fields[2]);
                SetObj(so, "flightField", fields[3]);
                SetObj(so, "shapeField", fields[4]);
            }

            so.ApplyModifiedPropertiesWithoutUndo();
            tester.ApplyProxyModifications();

            PushPriceLabels(tester, priceLabels);
        }

        /// <summary>価格表示用のテキストを配列で入れる</summary>
        private static void PushPriceLabels(MyDartsPartsTester tester, TextMeshProUGUI[] labels)
        {
            if (tester == null || labels == null) return;

            var f = typeof(MyDartsPartsTester).GetField(
                "priceLabels", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return;

            tester.UpdateProxy();
            f.SetValue(tester, labels);
            tester.ApplyProxyModifications();
            EditorUtility.SetDirty(tester);
        }

        private void WireSender(MyDartsSender sender,
                                MyDartsFetcher fetcher,
                                MyDartsStatusPanel status,
                                MyDartsDebugPanel debug,
                                params Button[] lockButtons)
        {
            if (sender == null) return;
            sender.UpdateProxy();

            var so = new SerializedObject(sender);
            SetObj(so, "fetcher", fetcher);
            SetObj(so, "statusPanel", status);
            SetObj(so, "debugPanel", debug);
            so.ApplyModifiedPropertiesWithoutUndo();

            sender.ApplyProxyModifications();

            PushLockButtons(sender, lockButtons);
            PushSenderUrls(sender);
        }

        /// <summary>
        /// 送信中に押せなくするボタンを登録する。
        /// 配列なので SerializedObject より直接入れたほうが早い。
        /// </summary>
        private static void PushLockButtons(MyDartsSender sender, Button[] buttons)
        {
            if (sender == null) return;

            var list = new List<Button>();
            if (buttons != null)
            {
                foreach (var b in buttons)
                {
                    if (b != null) list.Add(b);
                }
            }

            var f = typeof(MyDartsSender).GetField(
                "lockButtons", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (f == null) return;

            sender.UpdateProxy();
            f.SetValue(sender, list.ToArray());
            sender.ApplyProxyModifications();
            EditorUtility.SetDirty(sender);
        }

        private void WireFetcher(MyDartsFetcher fetcher,
                                MyDartsStatusPanel status,
                                MyDartsDebugPanel debug,
                                GameObject registerGroup)
        {
            if (fetcher == null) return;
            fetcher.UpdateProxy();

            var so = new SerializedObject(fetcher);
            SetObj(so, "statusPanel", status);
            SetObj(so, "debugPanel", debug);
            SetObj(so, "registerGroup", registerGroup);
            so.ApplyModifiedPropertiesWithoutUndo();

            fetcher.ApplyProxyModifications();

            PushLedgerUrl(fetcher);
        }

        /// <summary>
        /// 台帳URLを流し込む。
        /// VRCUrl は SerializedObject から素直に触れないので、
        /// プロキシのフィールドへ直接入れてから Udon に反映する。
        /// </summary>
        private void PushLedgerUrl(MyDartsFetcher fetcher)
        {
            if (fetcher == null) return;

            var url = _settings.ledgerUrl;
            if (string.IsNullOrEmpty(url))
            {
                var b = _settings.gasBaseUrl;
                if (string.IsNullOrEmpty(b)) return;
                url = b + (b.Contains("?") ? "&" : "?") + "all=1";
            }

            var f = typeof(MyDartsFetcher).GetField(
                "ledgerUrl", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            if (f == null)
            {
                Debug.LogWarning("[MyDarts] ledgerUrl のフィールドが見つかりませんでした。Inspectorで手動設定してください。");
                return;
            }

            fetcher.UpdateProxy();
            f.SetValue(fetcher, new VRCUrl(url));
            fetcher.ApplyProxyModifications();
            EditorUtility.SetDirty(fetcher);
        }

        // 焼き込むURLの本数。GAS側の仕様と揃えること
        //   a = 番号上2桁(0〜29) × 10 + 種別(1〜9)
        //   b = 番号下2桁(0〜99)
        //   v = 値(0〜199)
        private const int UrlCountA = 300;
        private const int UrlCountB = 100;
        private const int UrlCountV = 200;

        // w = 種別(0〜11) × 200 + 値(0〜199)。1本で送るとき用。
        // 10・11 はガチャなので、コイン減算×100（種別9）まで含めて 2400 本必要。
        private const int UrlCountW = 2400;

        /// <summary>
        /// 送信用のURLを全部作って流し込む。
        ///
        /// Udonは実行時にURLを組み立てられないので、
        /// 取り得る値の分だけVRCUrlを用意しておくしかない。
        /// 3本に分けてあるので 300+100+200 = 600本で済んでいる。
        /// （1本にまとめると 30×9×100×200 = 540万本になって不可能）
        /// </summary>
        private void PushSenderUrls(MyDartsSender sender)
        {
            if (sender == null) return;

            var b = _settings.gasBaseUrl;
            if (string.IsNullOrEmpty(b) || b.Contains("XXXXXXXX"))
            {
                Debug.LogWarning("[MyDarts] 送信先URLが未設定なので、送信用URLは焼き込みませんでした。");
                return;
            }

            var sep = b.Contains("?") ? "&" : "?";

            var a = new VRCUrl[UrlCountA];
            for (var i = 0; i < UrlCountA; i++) a[i] = new VRCUrl(b + sep + "a=" + i);

            var bb = new VRCUrl[UrlCountB];
            for (var i = 0; i < UrlCountB; i++) bb[i] = new VRCUrl(b + sep + "b=" + i);

            var v = new VRCUrl[UrlCountV];
            for (var i = 0; i < UrlCountV; i++) v[i] = new VRCUrl(b + sep + "v=" + i);

            var w = new VRCUrl[UrlCountW];
            for (var i = 0; i < UrlCountW; i++) w[i] = new VRCUrl(b + sep + "w=" + i);

            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var fa = typeof(MyDartsSender).GetField("aUrls", flags);
            var fb = typeof(MyDartsSender).GetField("bUrls", flags);
            var fv = typeof(MyDartsSender).GetField("vUrls", flags);
            var fw = typeof(MyDartsSender).GetField("wUrls", flags);

            if (fa == null || fb == null || fv == null || fw == null)
            {
                Debug.LogWarning("[MyDarts] 送信用URLのフィールドが見つかりませんでした。");
                return;
            }

            sender.UpdateProxy();
            fa.SetValue(sender, a);
            fb.SetValue(sender, bb);
            fv.SetValue(sender, v);
            fw.SetValue(sender, w);
            sender.ApplyProxyModifications();
            EditorUtility.SetDirty(sender);

            Debug.Log("[MyDarts] 送信用URLを "
                      + (UrlCountA + UrlCountB + UrlCountV + UrlCountW) + " 本 焼き込みました。");
        }

        /// <summary>名前でボタンを探して、画面切り替えの動作を割り当てる</summary>
        private static void BindScreenButton(GameObject root, string buttonName,
                                             MyDartsScreens screens, string eventName)
        {
            var btn = FindByName<Button>(root.transform, buttonName);
            if (btn != null) BindButton(btn, screens, eventName);
        }

        private static GameObject FindChildByName(Transform root, string name)
        {
            var all = root.GetComponentsInChildren<Transform>(true);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.name == name) return all[i].gameObject;
            }
            return null;
        }

        private static void WireStatus(MyDartsStatusPanel status, TextMeshProUGUI title, TextMeshProUGUI body)
        {
            if (status == null) return;
            status.UpdateProxy();

            var so = new SerializedObject(status);
            SetObj(so, "titleText", title);
            SetObj(so, "bodyText", body);
            so.ApplyModifiedPropertiesWithoutUndo();

            status.ApplyProxyModifications();
        }

        private void WireDebug(MyDartsDebugPanel debug, TextMeshProUGUI log, TextMeshProUGUI state)
        {
            if (debug == null) return;
            debug.UpdateProxy();

            var so = new SerializedObject(debug);
            SetObj(so, "logText", log);
            SetObj(so, "stateText", state);
            SetInt(so, "maxLines", _settings.debugMaxLines);
            so.ApplyModifiedPropertiesWithoutUndo();

            debug.ApplyProxyModifications();
        }

        private void PushSettingsTo(MyDartsRegister register)
        {
            if (register == null) return;
            register.UpdateProxy();

            var so = new SerializedObject(register);
            SetStr(so, "gasBaseUrl", _settings.gasBaseUrl);
            SetInt(so, "initialTip", _settings.initialTip);
            SetInt(so, "initialBarrel", _settings.initialBarrel);
            SetInt(so, "initialShaft", _settings.initialShaft);
            SetInt(so, "initialFlight", _settings.initialFlight);
            SetInt(so, "initialFlightShape", _settings.initialFlightShape);
            so.ApplyModifiedPropertiesWithoutUndo();

            register.ApplyProxyModifications();
        }

        private static void SetObj(SerializedObject so, string name, Object value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.objectReferenceValue = value;
        }

        private static void SetStr(SerializedObject so, string name, string value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.stringValue = value;
        }

        private static void SetInt(SerializedObject so, string name, int value)
        {
            var p = so.FindProperty(name);
            if (p != null) p.intValue = value;
        }

        // ============================================================
        //  ボタン → Udon のイベント登録
        // ============================================================
        /// <summary>
        /// ポインタ用の割り当てをしつつ、指でも押せるように控えておく。
        /// 筐体の中のボタンだけこちらを使う（デバッグ用の板は対象外）。
        /// </summary>
        private void BindTouch(Button button, UdonSharpBehaviour target, string eventName)
        {
            BindButton(button, target, eventName);

            if (button == null || target == null) return;
            _touchButtons.Add(button);
            _touchTargets.Add(target);
            _touchEvents.Add(eventName);
        }

        private static void BindButton(Button button, UdonSharpBehaviour target, string eventName)
        {
            if (button == null || target == null) return;

            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(target);
            if (ub == null)
            {
                Debug.LogWarning("[MyDarts] UdonBehaviourが取得できませんでした: " + eventName);
                return;
            }

            // 既存の登録を消してから付け直す（重複防止）
            for (var i = button.onClick.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                UnityEventTools.RemovePersistentListener(button.onClick, i);
            }

            UnityEventTools.AddStringPersistentListener(
                button.onClick,
                new UnityAction<string>(ub.SendCustomEvent),
                eventName);
        }

        private static void BindUrlInput(VRCUrlInputField field, MyDartsRegister target, string eventName)
        {
            if (field == null || target == null) return;

            var ub = UdonSharpEditorUtility.GetBackingUdonBehaviour(target);
            if (ub == null) return;

            // VRCUrlInputField は TMP_InputField でも UI.InputField でもない独自実装なので、
            // onEndEdit はリフレクションで取り出す。
            var ev = FindSubmitEvent(field);
            if (ev == null)
            {
                Debug.LogWarning(
                    "[MyDarts] VRCUrlInputField の OnEndEdit を取得できませんでした。\n" +
                    "Inspectorで UrlInput の On End Edit に " +
                    "Register(UdonBehaviour) → SendCustomEvent / 引数 " + eventName + " を設定してください。");
                return;
            }

            for (var i = ev.GetPersistentEventCount() - 1; i >= 0; i--)
            {
                UnityEventTools.RemovePersistentListener(ev, i);
            }

            UnityEventTools.AddStringPersistentListener(
                ev,
                new UnityAction<string>(ub.SendCustomEvent),
                eventName);
        }

        /// <summary>VRCUrlInputField から UnityEvent&lt;string&gt; な onEndEdit を探す</summary>
        private static UnityEvent<string> FindSubmitEvent(VRCUrlInputField field)
        {
            var type = field.GetType();
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            var prop = type.GetProperty("onEndEdit", flags);
            if (prop != null)
            {
                var v = prop.GetValue(field) as UnityEvent<string>;
                if (v != null) return v;
            }

            var names = new[] { "m_OnEndEdit", "onEndEdit", "m_OnSubmit" };
            for (var i = 0; i < names.Length; i++)
            {
                var f = type.GetField(names[i], flags);
                if (f == null) continue;
                var v = f.GetValue(field) as UnityEvent<string>;
                if (v != null) return v;
            }

            return null;
        }

        // ============================================================
        //  UI生成ヘルパ
        // ============================================================
        private static GameObject NewChild(Transform parent, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static GameObject CreateWorldCanvas(Transform parent, string name, Vector3 position, Vector2 sizeMeters)
        {
            var go = NewChild(parent, name);
            go.transform.position = position;

            // VRChatではCanvasのレイヤーを UI にすると触れなくなるので Default のままにする
            go.layer = 0;

            var canvas = go.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            go.AddComponent<CanvasScaler>();
            go.AddComponent<GraphicRaycaster>();

            // 1000px = 1m として扱う
            var rt = go.GetComponent<RectTransform>();
            rt.sizeDelta = new Vector2(sizeMeters.x * 1000f, sizeMeters.y * 1000f);
            rt.localScale = Vector3.one * 0.001f;

            // VRChatのポインタが当たるようにコライダーが要る（ローカル単位＝px換算のまま）
            var col = go.AddComponent<BoxCollider>();
            col.size = new Vector3(sizeMeters.x * 1000f, sizeMeters.y * 1000f, 10f);
            col.center = Vector3.zero;
            col.isTrigger = false;

            AddUiShape(go);

            return go;
        }

        /// <summary>
        /// VRC Ui Shape を付ける。これが無いとVRChatでUIを操作できない。
        /// SDKのバージョンで型の場所が変わる可能性があるので、名前で探して付ける。
        /// </summary>
        private static void AddUiShape(GameObject go)
        {
            System.Type t = null;
            var assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length && t == null; i++)
            {
                t = assemblies[i].GetType("VRC.SDK3.Components.VRCUiShape");
                if (t == null) t = assemblies[i].GetType("VRC.SDKBase.VRC_UiShape");
            }

            if (t == null)
            {
                Debug.LogWarning(
                    "[MyDarts] VRC Ui Shape が見つかりませんでした。" +
                    "Canvasに手動で追加してください。無いとVRChatで操作できません。");
                return;
            }

            if (go.GetComponent(t) == null) go.AddComponent(t);
        }

        /// <summary>UIの操作にはEventSystemが1つ必要。無ければ作る。</summary>
        private static void EnsureEventSystem(Transform parent)
        {
            if (Object.FindObjectOfType<UnityEngine.EventSystems.EventSystem>() != null) return;

            var go = NewChild(parent, "EventSystem");
            go.AddComponent<UnityEngine.EventSystems.EventSystem>();
            go.AddComponent<UnityEngine.EventSystems.StandaloneInputModule>();
            Debug.Log("[MyDarts] シーンにEventSystemが無かったので追加しました。");
        }

        private static GameObject CreatePanel(Transform parent, string name, Color color)
        {
            var go = NewChild(parent, name);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;

            var img = go.AddComponent<Image>();
            img.color = color;

            return go;
        }

        private static TextMeshProUGUI CreateText(Transform parent, string name, string text, float size, TextAlignmentOptions align)
        {
            var go = NewChild(parent, name);
            go.AddComponent<RectTransform>();

            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.alignment = align;
            t.color = ColText;
            t.enableWordWrapping = true;
            t.raycastTarget = false;

            return t;
        }

        private static Button CreateButton(Transform parent, string name, string label)
        {
            var go = NewChild(parent, name);
            go.AddComponent<RectTransform>();

            var img = go.AddComponent<Image>();
            img.color = ColPanel;

            var btn = go.AddComponent<Button>();
            btn.targetGraphic = img;

            // 押した時の色。青が差してから戻る
            var colors = btn.colors;
            colors.normalColor = Color.white;
            colors.highlightedColor = new Color(1.35f, 1.6f, 1.9f, 1f);
            colors.pressedColor = new Color(1.8f, 2.4f, 3f, 1f);
            colors.selectedColor = Color.white;
            colors.fadeDuration = 0.08f;
            btn.colors = colors;

            // 上端の細い光。これだけで平面が「物」に見える
            var top = NewChild(go.transform, "Edge");
            var topRt = top.AddComponent<RectTransform>();
            SetAnchors(topRt, new Vector2(0f, 1f), new Vector2(1f, 1f),
                       new Vector2(0f, -3f), Vector2.zero);
            var topImg = top.AddComponent<Image>();
            topImg.color = ColGlowDim;
            topImg.raycastTarget = false;

            var t = CreateText(go.transform, "Label", label, 30, TextAlignmentOptions.Center);
            t.color = ColText;
            t.fontStyle = FontStyles.Bold;
            SetAnchors(t.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            return btn;
        }

        /// <summary>
        /// ボタンの見た目をそろえる。
        /// primary は「進む・買う」など主役のボタン。青の縁を強くする。
        /// </summary>
        private static void StyleButton(Button button, bool primary)
        {
            if (button == null) return;

            var img = button.targetGraphic as Image;
            if (img != null) img.color = primary ? ColDeep : ColPanel;

            var outline = button.gameObject.GetComponent<Outline>();
            if (outline == null) outline = button.gameObject.AddComponent<Outline>();
            outline.effectColor = primary ? ColGlow : ColGlowDim;
            outline.effectDistance = new Vector2(2f, -2f);
            outline.useGraphicAlpha = false;

            var edge = button.transform.Find("Edge");
            if (edge != null)
            {
                var edgeImg = edge.GetComponent<Image>();
                if (edgeImg != null) edgeImg.color = primary ? ColGlow : ColGlowDim;
            }

            var label = button.transform.Find("Label");
            if (label != null)
            {
                var t = label.GetComponent<TextMeshProUGUI>();
                if (t != null) t.color = primary ? ColWhite : ColText;
            }
        }

        /// <summary>読み取り専用にも使える普通のTMP入力欄</summary>
        private static TMP_InputField CreateInputField(Transform parent, string name, string placeholder, bool editable)
        {
            var go = NewChild(parent, name);
            go.AddComponent<RectTransform>();

            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.12f);

            var field = go.AddComponent<TMP_InputField>();
            BuildInputFieldChildren(go, field, placeholder);
            field.readOnly = !editable;

            return field;
        }

        /// <summary>
        /// VRCUrlInputField を作る。
        ///
        /// VRCUrlInputField は TMP_InputField でも UnityEngine.UI.InputField でもなく、
        /// SDK内の独自実装（旧InputField相当。m_TextComponent / m_Placeholder を持つ）。
        /// なので子は旧UIのTextで組み、参照はSerializedObject経由で入れる。
        /// </summary>
        private static VRCUrlInputField CreateVrcUrlInputField(Transform parent, string name)
        {
            var go = NewChild(parent, name);
            go.AddComponent<RectTransform>();

            var img = go.AddComponent<Image>();
            img.color = new Color(1f, 1f, 1f, 0.18f);

            var vrcField = go.AddComponent<VRCUrlInputField>();

            var placeholder = CreateLegacyText(go.transform, "Placeholder", "Paste the URL here");
            placeholder.color = new Color(1f, 1f, 1f, 0.4f);
            placeholder.fontStyle = FontStyle.Italic;

            var text = CreateLegacyText(go.transform, "Text", "");
            text.supportRichText = false;

            var so = new SerializedObject(vrcField);
            SetObj(so, "m_TextComponent", text);
            SetObj(so, "m_Placeholder", placeholder);
            so.ApplyModifiedPropertiesWithoutUndo();

            // 入らなかった場合は名前が違う可能性があるので知らせる
            var check = new SerializedObject(vrcField);
            var tc = check.FindProperty("m_TextComponent");
            if (tc == null || tc.objectReferenceValue == null)
            {
                Debug.LogWarning(
                    "[MyDarts] VRCUrlInputField の Text 参照を自動設定できませんでした。\n" +
                    "Inspectorで UrlInput の Text / Placeholder に子オブジェクトを割り当ててください。");
            }

            return vrcField;
        }

        /// <summary>旧UIのText（VRCUrlInputFieldが要求する形式）</summary>
        private static Text CreateLegacyText(Transform parent, string name, string content)
        {
            var go = NewChild(parent, name);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(10, 6);
            rt.offsetMax = new Vector2(-10, -6);

            var t = go.AddComponent<Text>();
            t.text = content;
            t.font = LegacyFont();
            t.fontSize = 22;
            t.color = Color.white;
            t.alignment = TextAnchor.MiddleLeft;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Truncate;
            t.raycastTarget = false;

            return t;
        }

        private static Font LegacyFont()
        {
            // Unity 2022 以降は Arial.ttf が無いので LegacyRuntime.ttf を使う
            var f = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (f == null) f = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return f;
        }

        /// <summary>TMP_InputField に必要な子（表示領域・本文・プレースホルダ）を組む</summary>
        private static void BuildInputFieldChildren(GameObject root, TMP_InputField field, string placeholder)
        {
            var area = NewChild(root.transform, "Text Area");
            var areaRt = area.AddComponent<RectTransform>();
            areaRt.anchorMin = Vector2.zero;
            areaRt.anchorMax = Vector2.one;
            areaRt.offsetMin = new Vector2(10, 6);
            areaRt.offsetMax = new Vector2(-10, -6);
            area.AddComponent<RectMask2D>();

            var ph = CreateText(area.transform, "Placeholder", placeholder, 22, TextAlignmentOptions.Left);
            ph.color = new Color(1f, 1f, 1f, 0.4f);
            SetAnchors(ph.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            var txt = CreateText(area.transform, "Text", "", 22, TextAlignmentOptions.Left);
            SetAnchors(txt.rectTransform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);

            field.textViewport = areaRt;
            field.textComponent = txt;
            field.placeholder = ph;
            field.lineType = TMP_InputField.LineType.SingleLine;
            field.targetGraphic = root.GetComponent<Image>();
        }

        /// <summary>
        /// 親の上端を基準に、左右マージン付きで矩形を置く。
        /// offsetMin.y は必ず offsetMax.y より小さくないと高さがマイナスになって潰れる。
        /// </summary>
        private static void SetTopRect(RectTransform rt, float top, float height, float margin = 24f)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(margin, -(top + height));
            rt.offsetMax = new Vector2(-margin, -top);
        }

        /// <summary>
        /// 親の上端を基準に、横位置を割合で指定して置く。
        /// ラベル・入力欄・ボタンを1行に並べる用。
        /// </summary>
        private static void SetRowRect(RectTransform rt, float top, float height,
                                       float xMinFrac, float xMaxFrac, float pad = 8f)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(xMinFrac, 1f);
            rt.anchorMax = new Vector2(xMaxFrac, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = new Vector2(pad, -(top + height));
            rt.offsetMax = new Vector2(-pad, -top);
        }

        /// <summary>
        /// 親の下端を基準に、左半分か右半分へ置く。ボタンを2つ並べる用。
        /// </summary>
        private static void SetBottomHalfRect(RectTransform rt, float bottom, float height, bool leftHalf,
                                              float margin = 20f, float gap = 12f)
        {
            if (rt == null) return;
            rt.anchorMin = new Vector2(leftHalf ? 0f : 0.5f, 0f);
            rt.anchorMax = new Vector2(leftHalf ? 0.5f : 1f, 0f);
            rt.pivot = new Vector2(0.5f, 0f);
            rt.offsetMin = new Vector2(leftHalf ? margin : gap * 0.5f, bottom);
            rt.offsetMax = new Vector2(leftHalf ? -gap * 0.5f : -margin, bottom + height);
        }

        private static void SetAnchors(RectTransform rt, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax)
        {
            if (rt == null) return;
            rt.anchorMin = min;
            rt.anchorMax = max;
            rt.offsetMin = offsetMin;
            rt.offsetMax = offsetMax;
        }

        private static T FindByName<T>(Transform root, string name) where T : Component
        {
            var all = root.GetComponentsInChildren<T>(true);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i].gameObject.name == name) return all[i];
            }
            return null;
        }
    }
}
#endif
