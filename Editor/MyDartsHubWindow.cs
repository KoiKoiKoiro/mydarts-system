#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

namespace MyDartsSystem.EditorTools
{
    /// <summary>
    /// マイダーツの開発用エディター。入口はここ1つ。
    ///
    /// メニュー: MyDarts → マイダーツ
    ///
    ///   セットアップ … 必要なものを入れてボタン1つ。普段はここだけ
    ///   詳細         … 位置・値段・点検・試し塗りなど細かい調整
    ///   開発         … 自動コンパイル監視、通知音、後片づけ
    ///
    /// 「詳細」は元々別々のウィンドウだったものを、表示部分だけ借りて並べている。
    /// 中身のコードは共通なので、どちらから触っても同じ結果になる。
    /// </summary>
    public class MyDartsHubWindow : EditorWindow
    {
        // 監視ツールが使っている保存キー。
        // あちらは別のアセンブリにいるので、型を参照せずキーだけ共有する。
        private const string WatchKey = "MyDarts.DevWatch.Enabled";
        private const string SoundKey = "MyDarts.DevWatch.Sound";

        private static readonly string[] TabNames = { "セットアップ", "詳細", "開発" };

        [SerializeField] private int tab;
        [SerializeField] private bool alsoMakePreview = true;

        private MyDartsSetupWindow _setup;
        private MyDartsPartsSetupWindow _parts;

        private Vector2 _mainScroll;
        private Vector2 _devScroll;
        private int _detailTab;

        private string _lastResult = "";

        [MenuItem("MyDarts/マイダーツ")]
        public static void Open()
        {
            var w = GetWindow<MyDartsHubWindow>("マイダーツ");
            w.minSize = new Vector2(480, 600);
        }

        private void OnEnable() { EnsureTabs(); }

        private void EnsureTabs()
        {
            if (_setup == null) _setup = MyDartsSetupWindow.CreateEmbedded();
            if (_parts == null) _parts = MyDartsPartsSetupWindow.CreateEmbedded();
        }

        private void OnFocus()
        {
            EnsureTabs();
            if (_parts != null) _parts.Refresh();
        }

        private void OnGUI()
        {
            EnsureTabs();

            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            tab = GUILayout.Toolbar(tab, TabNames, GUILayout.Height(26));
            if (EditorGUI.EndChangeCheck() && _parts != null) _parts.Refresh();
            EditorGUILayout.Space(4);

            if (tab == 0) DrawMain();
            else if (tab == 1) DrawDetail();
            else DrawDev();
        }

        // ============================================================
        //  セットアップ（普段使う画面）
        // ============================================================
        private void DrawMain()
        {
            _mainScroll = EditorGUILayout.BeginScrollView(_mainScroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("必要なものを入れて、下のボタンを押すだけです。",
                EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(8);

            var settings = _setup.GetSettings();

            // ---- ① ダーツ ----
            EditorGUILayout.LabelField("① ダーツ", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("ヒエラルキーから CrecentDarts-PublicVariant をドラッグ",
                EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            var dart = (GameObject)EditorGUILayout.ObjectField(
                _parts.GetTarget(), typeof(GameObject), true, GUILayout.Height(36));
            if (EditorGUI.EndChangeCheck()) _parts.SetTarget(dart);

            if (_parts.GetTarget() == null && GUILayout.Button("シーンから探して入れる"))
            {
                _parts.FindCandidateFromScene();
            }

            EditorGUILayout.Space(10);

            // ---- ② 柄の画像 ----
            EditorGUILayout.LabelField("② 柄の画像", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("柄を格子状に並べた1枚の画像（8×8で64種）",
                EditorStyles.miniLabel);

            EditorGUI.BeginChangeCheck();
            var atlas = (Texture2D)EditorGUILayout.ObjectField(
                _parts.GetAtlas(), typeof(Texture2D), false,
                GUILayout.Height(64), GUILayout.Width(64));
            if (EditorGUI.EndChangeCheck()) _parts.SetAtlas(atlas);

            EditorGUILayout.Space(10);

            // ---- ③ つなぎ先 ----
            EditorGUILayout.LabelField("③ サーバー", EditorStyles.boldLabel);

            if (settings != null)
            {
                EditorGUI.BeginChangeCheck();
                settings.gasBaseUrl = EditorGUILayout.TextField(
                    new GUIContent("送信先", "登録用。末尾は /exec"), settings.gasBaseUrl);
                settings.ledgerUrl = EditorGUILayout.TextField(
                    new GUIContent("台帳の読み先",
                        "空でよい。空なら送信先URLに ?all=1 を付けたものを使う。\n" +
                        "別の場所（GitHub Pagesなど）から読ませたい時だけ入れる"),
                    settings.ledgerUrl);
                settings.gachaVideoUrl = EditorGUILayout.TextField(
                    new GUIContent("ガチャ演出の動画",
                        "直リンクの mp4 を推奨。空なら演出は動画なしで進む。\n" +
                        "遊ぶ人に「Untrusted URLs」をONにしてもらう必要がある"),
                    settings.gachaVideoUrl);

                if (EditorGUI.EndChangeCheck()) _setup.SaveSettings();
            }

            Line();

            // ---- 状態 ----
            EditorGUILayout.LabelField("いまの状態", EditorStyles.boldLabel);

            Status(_parts.GetTarget() != null, "ダーツが入っている");
            Status(_parts.GetAtlas() != null, "柄の画像が入っている");
            // 台帳の読み先は空でよい。空なら送信先URLに ?all=1 を付けたものを使う
            var hasLedger = settings != null &&
                            (!string.IsNullOrEmpty(settings.ledgerUrl) ||
                             !string.IsNullOrEmpty(settings.gasBaseUrl));

            Status(hasLedger, string.IsNullOrEmpty(settings == null ? null : settings.ledgerUrl)
                ? "台帳の読み先（送信先URLから自動で作る）"
                : "台帳の読み先が入っている");
            Status(_setup.HasRoot(), "一式が生成済み");
            Status(_parts.HasPreview(), "見本のダーツがある");

            if (_parts.GetTarget() != null)
            {
                EditorGUILayout.LabelField(_parts.Summary(), EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.Space(6);
            alsoMakePreview = EditorGUILayout.ToggleLeft(
                "見本のダーツも作る（コレクション画面の横に置く飾り）", alsoMakePreview);

            EditorGUILayout.Space(10);

            // ---- 実行 ----
            var ready = _parts.GetTarget() != null;

            EditorGUI.BeginDisabledGroup(!ready);
            GUI.backgroundColor = new Color(0.5f, 0.85f, 0.6f);
            if (GUILayout.Button("セットアップ", GUILayout.Height(46)))
            {
                RunSetup();
            }
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();

            if (!ready)
            {
                EditorGUILayout.LabelField("ダーツを入れると押せます。", EditorStyles.miniLabel);
            }
            else
            {
                EditorGUILayout.LabelField(
                    "何度押しても大丈夫です。作り直して繋ぎ直します。",
                    EditorStyles.wordWrappedMiniLabel);
            }

            if (!string.IsNullOrEmpty(_lastResult))
            {
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(_lastResult, MessageType.Info);
            }

            EditorGUILayout.Space(12);
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 一気に全部やる。
        ///
        ///   1. 一式を生成（無ければ）
        ///   2. 柄の部品をダーツへ取り付け
        ///   3. 見本のダーツを作る
        ///   4. 消えたスクリプトの通信記録を掃除
        ///
        /// 順番が大事で、生成のあとに取り付けないと参照が切れる。
        /// ここを間違えやすいので、ボタン1つにまとめてある。
        /// </summary>
        private void RunSetup()
        {
            var steps = "";

            if (!_setup.HasRoot())
            {
                _setup.GenerateAll();
                steps += "・一式を生成しました\n";
            }
            else if (_setup.NeedsRebuild())
            {
                // 昔の作り（画面が別々のCanvas）のままだと繋ぎ直しでは直らない
                _setup.GenerateAll();
                steps += "・作りが古かったので一式を作り直しました\n";
            }
            else
            {
                _setup.RelinkAll();
                steps += "・一式の参照を繋ぎ直しました\n";
            }

            _parts.Refresh();

            if (_parts.CanAttach())
            {
                _parts.AttachAll();
                steps += "・柄の部品をダーツに取り付けました\n";
            }
            else
            {
                steps += "・柄の取り付けは見送りました（詳細タブの赤い項目を確認してください）\n";
            }

            if (alsoMakePreview && _parts.GetTarget() != null)
            {
                _parts.CreatePreviewDart();
                steps += "・見本のダーツを作りました\n";
            }

            if (_parts.GetTarget() != null)
            {
                _parts.CreateStudioDart();
                steps += "・ガチャ演出用のダーツを撮影セットに置きました\n";
            }

            MyDartsPartsSetupWindow.CleanNetworkIDsQuiet();
            _parts.Refresh();

            _lastResult = steps + "\nPlay して確認してください。";
            Debug.Log("[MyDarts] セットアップ完了\n" + steps);
        }

        private static void Status(bool ok, string label)
        {
            EditorGUILayout.BeginHorizontal();
            GUI.color = ok ? new Color(0.45f, 0.9f, 0.55f) : new Color(0.7f, 0.7f, 0.7f);
            EditorGUILayout.LabelField(ok ? "済" : "未", EditorStyles.boldLabel, GUILayout.Width(26));
            GUI.color = Color.white;
            EditorGUILayout.LabelField(label);
            EditorGUILayout.EndHorizontal();
        }

        // ============================================================
        //  詳細
        // ============================================================
        private void DrawDetail()
        {
            _detailTab = GUILayout.Toolbar(_detailTab, new[] { "一式・配置", "柄" });
            EditorGUILayout.Space(4);

            if (_detailTab == 0) _setup.DrawGUI();
            else _parts.DrawGUI();
        }

        // ============================================================
        //  開発
        // ============================================================
        private void DrawDev()
        {
            _devScroll = EditorGUILayout.BeginScrollView(_devScroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("自動コンパイル監視", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "ファイルが変わったら自動で取り込み、結果を MyDartsCompileReport.txt に書きます。\n" +
                "再生中は取り込みを止めます。",
                EditorStyles.wordWrappedMiniLabel);

            var watch = EditorPrefs.GetBool(WatchKey, false);
            var next = EditorGUILayout.ToggleLeft("監視する", watch);
            if (next != watch) EditorPrefs.SetBool(WatchKey, next);

            var sound = EditorPrefs.GetBool(SoundKey, true);
            var nextSound = EditorGUILayout.ToggleLeft("変化があったら音を鳴らす", sound);
            if (nextSound != sound) EditorPrefs.SetBool(SoundKey, nextSound);

            EditorGUILayout.LabelField(
                "音は、こちらが書き換えたときだけ鳴ります。自分で編集したときは鳴りません。",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(6);
            if (GUILayout.Button("今すぐ取り込む")) AssetDatabase.Refresh();

            Line();

            EditorGUILayout.LabelField("後片づけ", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "スクリプトを消したあとに残る「実物が無い通信の記録」を片づけます。\n" +
                "再生する直前に自動でも走るので、普段は押す必要はありません。",
                EditorStyles.wordWrappedMiniLabel);

            if (GUILayout.Button("ネットワークIDを掃除する"))
            {
                MyDartsPartsSetupWindow.CleanNetworkIDs();
            }

            Line();

            EditorGUILayout.LabelField("覚え書き", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "・柄の番号はスプレッドシートの台帳が正本です\n" +
                "・確率はレア度表の「重み」だけで変えられます（デプロイ不要）\n" +
                "・値段は価格表のC列だけで変えられます\n" +
                "・ダーツギミック本体のファイルには一切触っていません",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space(12);
            EditorGUILayout.EndScrollView();
        }

        private static void Line()
        {
            EditorGUILayout.Space(8);
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.5f));
            EditorGUILayout.Space(8);
        }
    }
}
#endif
