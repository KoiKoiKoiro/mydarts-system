#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;

using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UdonSharp;
using UdonSharpEditor;
using VRC.SDKBase;

namespace MyDartsSystem.EditorTools
{
    /// <summary>
    /// 柄をダーツに取り付けるための画面。
    ///
    /// メニュー: MyDarts → 柄のセットアップ
    ///
    /// 使い方
    ///   1. ダーツ一式をこの画面へドラッグする
    ///   2. 点検結果を見る
    ///   3. 「取り付ける」を押す
    ///
    /// ダーツギミック本体のファイルには一切触らない。
    /// あちらの中身を読むときも「型」ではなく「名前」で探しているので、
    /// このパッケージ単体でもコンパイルが通る。
    /// </summary>
    public class MyDartsPartsSetupWindow : EditorWindow
    {
        // ---- ダーツギミック側の名前。名前でしか触らない ----
        private const string TypeDart = "CrecentDartPublicVariant";
        private const string TypeDartReal = "CrecentDartRealPublicVariant";
        private const string TypeHolder = "CrecentDartHolderPublicVariant";
        private const string TypeMachine = "CrecentDartMachinePublicVariant";

        private const string FieldBarrelVariants = "barrelVariants";
        private const string FieldFlightVariants = "flightVariants";
        private const string FieldShaftVariants = "shaftVariants";
        private const string FieldTipRenderers = "tipColorRenderers";
        private const string FieldHolderDarts = "holderDarts";
        private const string FieldHolderRealDarts = "holderRealDarts";

        /// <summary>
        /// 受け付けるプレハブ。これ以外は取り付けさせない。
        /// 通常版とLIVE版は、ホルダー1・ダーツ3・マシン1で構成が同じなので両方使える。
        /// </summary>
        private static readonly string[] AllowedPrefabs =
        {
            "CrecentDarts-PublicVariant",
            "CrecentDarts-LIVE-PublicVariant",
        };

        private const string LibraryObjectName = "[MyDarts] Skin Library";

        private Vector2 _scroll;

        private readonly List<DartRow> _darts = new List<DartRow>();
        private readonly List<HolderRow> _holders = new List<HolderRow>();
        private readonly List<Check> _checks = new List<Check>();

        private MyDartsSkinLibrary _library;
        private MyDartsFetcher _fetcher;
        private bool _scanned;

        /// <summary>
        /// 連携するダーツ。ここに入れたものの中だけを見る。
        /// [SerializeField] にしてあるので、コンパイルが走っても消えない。
        /// </summary>
        [SerializeField] private GameObject target;

        // ---- 柄の設定。入れ物がまだ無くても、ここで先に決めておける ----
        [SerializeField] private Texture2D atlas;
        [SerializeField] private int gridX = 8;
        [SerializeField] private int gridY = 8;
        [SerializeField] private int designCount = 64;
        [SerializeField] private float inset = 0.002f;
        [SerializeField] private bool whitenBarrelTint = true;
        [SerializeField] private int colorableBelow = 40;

        [SerializeField] private int testDesign;

        // ---- 見本のダーツ ----
        [SerializeField] private float previewScale = 4f;
        [SerializeField] private Vector3 previewOffset = new Vector3(-0.9f, 0f, 0f);

        private const string PreviewObjectName = "[MyDarts] Preview Dart";
        private const string StudioDartName = "[MyDarts] Studio Dart";

        private string _sourcePrefab = "";

        private class DartRow
        {
            public GameObject go;
            public Component crecent;
            public MyDartsSkin skin;

            // 塗る相手。数だけでなく中身も点検に使う
            public Renderer[] tipR, barrelR, shaftR, flightR;

            public int nullVariants;      // 形の入れ物に空欄がいくつあるか
            public bool missingScript;    // 壊れたスクリプトが付いていないか

            public int tip { get { return tipR == null ? 0 : tipR.Length; } }
            public int barrel { get { return barrelR == null ? 0 : barrelR.Length; } }
            public int shaft { get { return shaftR == null ? 0 : shaftR.Length; } }
            public int flight { get { return flightR == null ? 0 : flightR.Length; } }

            public bool AllPainted { get { return tip > 0 && barrel > 0 && shaft > 0 && flight > 0; } }

            public IEnumerable<Renderer> AllRenderers()
            {
                foreach (var r in tipR) yield return r;
                foreach (var r in barrelR) yield return r;
                foreach (var r in shaftR) yield return r;
                foreach (var r in flightR) yield return r;
            }
        }

        private class HolderRow
        {
            public GameObject go;
            public Component crecent;
            public MyDartsHolderHook hook;
            public int dartCount;
            public bool hasPickup;

            // 付いているだけでなく、中の参照が生きているか
            public bool wired;
        }

        /// <summary>点検の1項目</summary>
        private class Check
        {
            public bool ok;
            public bool blocking;   // これが駄目なら取り付けさせない
            public string text;
        }

        /// <summary>
        /// まとめ画面から中身だけ borrowed される。
        /// 単体のウィンドウとしては開かない（入口は MyDarts → マイダーツ）。
        /// </summary>
        public static MyDartsPartsSetupWindow CreateEmbedded()
        {
            var w = CreateInstance<MyDartsPartsSetupWindow>();
            w.Scan();
            return w;
        }

        private void OnFocus() { Scan(); }

        /// <summary>まとめ画面が表に出たときに呼ぶ</summary>
        public void Refresh() { Scan(); }

        // ============================================================
        //  まとめ画面から使う入口
        // ============================================================

        /// <summary>連携するダーツを外から入れる</summary>
        public void SetTarget(GameObject go)
        {
            target = SnapToPrefabRoot(go);
            Scan();
        }
        public GameObject GetTarget() { return target; }

        /// <summary>柄の画像を外から入れる</summary>
        public void SetAtlas(Texture2D tex)
        {
            atlas = tex;
            PushLibrary();
            BuildChecks();
        }
        public Texture2D GetAtlas() { return atlas; }

        /// <summary>取り付けられる状態か（赤が無いか）</summary>
        public bool CanAttach()
        {
            if (target == null || _checks.Count == 0) return false;
            foreach (var c in _checks) if (!c.ok && c.blocking) return false;
            return true;
        }

        /// <summary>点検の要約。まとめ画面に出す</summary>
        public string Summary()
        {
            if (target == null) return "ダーツが入っていません。";

            var ng = 0;
            var warn = 0;
            foreach (var c in _checks)
            {
                if (c.ok) continue;
                if (c.blocking) ng++; else warn++;
            }

            if (ng > 0) return "直すところが " + ng + " 件あります。詳細タブで確認してください。";
            if (warn > 0) return "ダーツ " + _darts.Count + "本。注意が " + warn + " 件（進められます）。";
            return "ダーツ " + _darts.Count + "本。問題ありません。";
        }

        public void AttachAll() { Attach(); }
        public void CreatePreviewDart() { CreatePreview(); }
        public void CreateStudioDart() { CreateStudio(); }
        public bool HasStudioDart() { return GameObject.Find(StudioDartName) != null; }
        public bool HasPreview() { return GameObject.Find(PreviewObjectName) != null; }

        // ============================================================
        //  画面
        // ============================================================
        private void OnGUI() { DrawGUI(); }

        /// <summary>まとめ画面からも呼べるように、中身を切り出してある</summary>
        public void DrawGUI()
        {
            if (!_scanned) Scan();

            // 窓が小さくても全部見られるように、画面ごと巻物にする
            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("ダーツに柄を乗せる", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "ダーツギミック本体には手を加えません。部品を足すだけです。",
                EditorStyles.wordWrappedMiniLabel);

            Line();
            DrawTarget();

            Line();
            DrawChecks();

            Line();
            DrawLibrary();

            Line();
            DrawFound();

            Line();
            DrawButtons();

            EditorGUILayout.Space(12);
            EditorGUILayout.EndScrollView();
        }

        private void DrawTarget()
        {
            EditorGUILayout.LabelField("① 連携するダーツを入れる", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "ヒエラルキーから CrecentDarts-PublicVariant をここへドラッグしてください。",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUI.BeginChangeCheck();
            var next = (GameObject)EditorGUILayout.ObjectField(
                target, typeof(GameObject), true, GUILayout.Height(38));

            if (EditorGUI.EndChangeCheck())
            {
                target = SnapToPrefabRoot(next);
                Scan();
            }

            if (target != null && !string.IsNullOrEmpty(_sourcePrefab))
            {
                EditorGUILayout.LabelField("元のプレハブ: " + _sourcePrefab, EditorStyles.miniLabel);
            }

            if (target == null && GUILayout.Button("シーンから探して入れる"))
            {
                FindCandidate();
            }
        }

        /// <summary>
        /// 子どもを入れられても、プレハブの一番上に直す。
        /// 毎回「一番上を入れてください」と言うより、こちらで直したほうが早い。
        /// </summary>
        private static GameObject SnapToPrefabRoot(GameObject go)
        {
            if (go == null) return null;
            if (EditorUtility.IsPersistent(go)) return go; // プレハブ本体はここでは直さない

            var root = PrefabUtility.GetOutermostPrefabInstanceRoot(go);
            return root != null ? root : go;
        }

        private void DrawChecks()
        {
            EditorGUILayout.LabelField("② 点検", EditorStyles.boldLabel);

            if (_checks.Count == 0)
            {
                EditorGUILayout.LabelField("ダーツを入れると点検します。", EditorStyles.miniLabel);
                return;
            }

            foreach (var c in _checks)
            {
                EditorGUILayout.BeginHorizontal();

                if (c.ok)
                {
                    GUI.color = new Color(0.45f, 0.9f, 0.55f);
                    EditorGUILayout.LabelField("OK", EditorStyles.boldLabel, GUILayout.Width(30));
                }
                else if (c.blocking)
                {
                    GUI.color = new Color(1f, 0.45f, 0.45f);
                    EditorGUILayout.LabelField("NG", EditorStyles.boldLabel, GUILayout.Width(30));
                }
                else
                {
                    GUI.color = new Color(1f, 0.85f, 0.4f);
                    EditorGUILayout.LabelField("注意", EditorStyles.boldLabel, GUILayout.Width(30));
                }
                GUI.color = Color.white;

                EditorGUILayout.LabelField(c.text, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndHorizontal();
            }
        }

        private void DrawLibrary()
        {
            EditorGUILayout.LabelField("③ 柄の画像", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "ここに入れた設定は、取り付けたときにそのままシーンへ渡ります。",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUI.BeginChangeCheck();

            // 画像は放り込みやすいように大きめの枠にする
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(
                new GUIContent("アトラス画像", "柄を格子状に並べた1枚の画像"),
                GUILayout.Width(100));
            atlas = (Texture2D)EditorGUILayout.ObjectField(
                atlas, typeof(Texture2D), false, GUILayout.Height(58), GUILayout.Width(58));
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            gridX = EditorGUILayout.IntSlider(new GUIContent("横のマス数"), gridX, 1, 16);
            gridY = EditorGUILayout.IntSlider(new GUIContent("縦のマス数"), gridY, 1, 16);
            EditorGUILayout.EndHorizontal();

            designCount = EditorGUILayout.IntSlider(new GUIContent("柄の数"), designCount, 1, 256);
            inset = EditorGUILayout.Slider(
                new GUIContent("端の余白", "隣のマスがにじむときに上げる"), inset, 0f, 0.02f);
            whitenBarrelTint = EditorGUILayout.Toggle(
                new GUIContent("バレルの色を白に戻す"), whitenBarrelTint);
            colorableBelow = EditorGUILayout.IntSlider(
                new GUIContent("色が乗る柄の範囲",
                    "この番号より前の柄には、プレイヤーが選んだ色が乗る。\n" +
                    "それ以降は白に戻して、柄をそのまま見せる"),
                colorableBelow, 0, 256);

            if (EditorGUI.EndChangeCheck())
            {
                PushLibrary();   // 入れ物があればその場で反映する
                BuildChecks();
            }

            if (_library == null)
            {
                EditorGUILayout.LabelField(
                    "画像の入れ物はまだシーンにありません。「取り付ける」を押すと作られ、上の設定が入ります。",
                    EditorStyles.wordWrappedMiniLabel);
            }
        }

        /// <summary>この画面の設定を、シーンの入れ物へ書き込む</summary>
        private void PushLibrary()
        {
            if (_library == null) return;

            var so = new SerializedObject(_library);
            so.FindProperty("atlas").objectReferenceValue = atlas;
            so.FindProperty("gridX").intValue = gridX;
            so.FindProperty("gridY").intValue = gridY;
            so.FindProperty("designCount").intValue = designCount;
            so.FindProperty("inset").floatValue = inset;
            so.FindProperty("whitenBarrelTint").boolValue = whitenBarrelTint;
            so.FindProperty("colorableBelow").intValue = colorableBelow;
            so.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(_library);
        }

        /// <summary>シーンの入れ物の設定を、この画面へ読み込む</summary>
        private void PullLibrary()
        {
            if (_library == null) return;

            var so = new SerializedObject(_library);
            atlas = so.FindProperty("atlas").objectReferenceValue as Texture2D;
            gridX = so.FindProperty("gridX").intValue;
            gridY = so.FindProperty("gridY").intValue;
            designCount = so.FindProperty("designCount").intValue;
            inset = so.FindProperty("inset").floatValue;
            whitenBarrelTint = so.FindProperty("whitenBarrelTint").boolValue;
            colorableBelow = so.FindProperty("colorableBelow").intValue;
        }

        private void DrawFound()
        {
            EditorGUILayout.LabelField("④ 中身", EditorStyles.boldLabel);

            if (target == null)
            {
                EditorGUILayout.LabelField("上の欄にダーツを入れると、中身がここに出ます。",
                    EditorStyles.miniLabel);
                return;
            }

            if (_holders.Count == 0 && _darts.Count == 0)
            {
                EditorGUILayout.LabelField("中にダーツが見つかりません。", EditorStyles.miniLabel);
                return;
            }

            foreach (var h in _holders) DrawRow(h.go, h.hook != null && h.wired,
                "ホルダー / ダーツ " + h.dartCount + "本"
                + (h.hasPickup ? "" : " / 握れない?")
                + (h.hook != null && !h.wired ? " / 繋がっていません" : ""));

            foreach (var d in _darts) DrawRow(d.go, d.skin != null,
                "チップ" + d.tip + " バレル" + d.barrel + " シャフト" + d.shaft + " 羽" + d.flight);

            EditorGUILayout.LabelField(
                "数字は「塗る相手が見つかった数」です。0があるとそこだけ柄が出ません。",
                EditorStyles.wordWrappedMiniLabel);
        }

        private static void DrawRow(GameObject go, bool attached, string note)
        {
            EditorGUILayout.BeginHorizontal("box");

            GUI.color = attached ? new Color(0.5f, 1f, 0.65f) : Color.white;
            EditorGUILayout.LabelField(attached ? "済" : "未", GUILayout.Width(26));
            GUI.color = Color.white;

            if (GUILayout.Button(go.name, EditorStyles.label))
            {
                Selection.activeGameObject = go;
                EditorGUIUtility.PingObject(go);
            }

            EditorGUILayout.LabelField(note, EditorStyles.miniLabel, GUILayout.Width(200));
            EditorGUILayout.EndHorizontal();
        }

        private void DrawButtons()
        {
            var blocked = false;
            foreach (var c in _checks) if (!c.ok && c.blocking) blocked = true;
            var canAttach = target != null && _checks.Count > 0 && !blocked;

            EditorGUI.BeginDisabledGroup(!canAttach);
            GUI.backgroundColor = new Color(0.55f, 0.85f, 0.6f);
            if (GUILayout.Button("取り付ける / 繋ぎ直す", GUILayout.Height(34))) Attach();
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();

            if (blocked)
            {
                EditorGUILayout.LabelField("赤い項目を直すまで押せません。",
                    EditorStyles.wordWrappedMiniLabel);
            }
            else
            {
                EditorGUILayout.LabelField("何度押しても大丈夫です。すでに付いていれば繋ぎ直すだけです。",
                    EditorStyles.wordWrappedMiniLabel);
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("もう一度調べる")) Scan();

            // ---- 動作確認 ----
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("柄の試し塗り", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "再生せずに、その場で柄を乗せて確かめられます。",
                EditorStyles.wordWrappedMiniLabel);

            EditorGUI.BeginDisabledGroup(_darts.Count == 0 || atlas == null);

            testDesign = EditorGUILayout.IntSlider(
                new GUIContent("柄番号"), testDesign, 0, Mathf.Max(0, designCount - 1));

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("この番号で試す", GUILayout.Height(26))) PreviewPaint(testDesign);
            if (GUILayout.Button("元に戻す", GUILayout.Height(26))) PreviewClear();
            EditorGUILayout.EndHorizontal();

            EditorGUI.EndDisabledGroup();

            if (atlas == null)
            {
                EditorGUILayout.LabelField("画像を入れると試せます。", EditorStyles.miniLabel);
            }

            // ---- 見本のダーツ ----
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("見本のダーツ", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "コレクション画面の横に置く、飾り専用のダーツです。\n" +
                "選んでいる途中の組み合わせが映ります。掴めず、当たり判定もありません。",
                EditorStyles.wordWrappedMiniLabel);

            previewScale = EditorGUILayout.Slider(
                new GUIContent("大きさ", "本物の何倍にするか"), previewScale, 1f, 12f);
            previewOffset = EditorGUILayout.Vector3Field(
                new GUIContent("置く位置", "コレクション画面からのずれ（m）"), previewOffset);

            var hasPreview = GameObject.Find(PreviewObjectName) != null;

            EditorGUI.BeginDisabledGroup(_darts.Count == 0);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button(hasPreview ? "見本を作り直す" : "見本のダーツを作る", GUILayout.Height(26)))
            {
                CreatePreview();
            }
            EditorGUI.BeginDisabledGroup(!hasPreview);
            if (GUILayout.Button("見本を消す", GUILayout.Height(26), GUILayout.Width(110)))
            {
                RemovePreview();
            }
            EditorGUI.EndDisabledGroup();
            EditorGUILayout.EndHorizontal();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(8);
            EditorGUI.BeginDisabledGroup(target == null);
            GUI.backgroundColor = new Color(0.9f, 0.6f, 0.6f);
            if (GUILayout.Button("取り外す"))
            {
                if (EditorUtility.DisplayDialog("取り外し",
                        "いま入れているダーツから、柄の部品を外します。\nダーツギミック本体はそのままです。",
                        "外す", "やめる"))
                {
                    Detach();
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUI.EndDisabledGroup();
        }

        private static void Line()
        {
            EditorGUILayout.Space(6);
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.5f));
            EditorGUILayout.Space(6);
        }

        // ============================================================
        //  調べる
        // ============================================================
        private void Scan()
        {
            _darts.Clear();
            _holders.Clear();
            _scanned = true;
            _sourcePrefab = "";

            var hadLibrary = _library != null;
            _library = Object.FindObjectOfType<MyDartsSkinLibrary>(true);
            _fetcher = Object.FindObjectOfType<MyDartsFetcher>(true);

            // シーンに入れ物が現れた（あるいは開き直した）ときは、そちらの値に合わせる
            if (_library != null && !hadLibrary) PullLibrary();

            if (target != null && !EditorUtility.IsPersistent(target))
            {
                _sourcePrefab = GetSourcePrefabName(target);

                var all = target.GetComponentsInChildren<UdonSharpBehaviour>(true);
                foreach (var c in all)
                {
                    if (c == null) continue;
                    var n = c.GetType().Name;

                    if (n == TypeDart || n == TypeDartReal)
                    {
                        var row = new DartRow();
                        row.go = c.gameObject;
                        row.crecent = c;
                        row.skin = c.GetComponent<MyDartsSkin>();

                        var so = new SerializedObject(c);
                        row.tipR = CollectRenderers(so, FieldTipRenderers);
                        row.barrelR = CollectFromVariants(so, FieldBarrelVariants);
                        row.shaftR = CollectFromVariants(so, FieldShaftVariants);
                        row.flightR = CollectFromVariants(so, FieldFlightVariants);

                        row.nullVariants =
                            CountNulls(so, FieldBarrelVariants) +
                            CountNulls(so, FieldShaftVariants) +
                            CountNulls(so, FieldFlightVariants) +
                            CountNulls(so, FieldTipRenderers);

                        row.missingScript = HasMissingScript(row.go);

                        _darts.Add(row);
                    }
                    else if (n == TypeHolder)
                    {
                        var row = new HolderRow();
                        row.go = c.gameObject;
                        row.crecent = c;
                        row.hook = c.GetComponent<MyDartsHolderHook>();
                        row.hasPickup = c.GetComponent(typeof(VRC_Pickup)) != null;

                        // 付いていても、作り直しで参照が切れていることがある。
                        // 中を覗いて生きているか確かめる
                        if (row.hook != null)
                        {
                            var hso = new SerializedObject(row.hook);
                            var f = hso.FindProperty("fetcher");
                            var ds = hso.FindProperty("darts");
                            row.wired = f != null && f.objectReferenceValue != null
                                     && ds != null && ds.isArray && ds.arraySize > 0;
                        }

                        var so = new SerializedObject(c);
                        row.dartCount = CountArray(so, FieldHolderDarts)
                                      + CountArray(so, FieldHolderRealDarts);

                        _holders.Add(row);
                    }
                }
            }

            BuildChecks();
            Repaint();
        }

        /// <summary>元になったプレハブの名前。プレハブから作られていなければ空</summary>
        private static string GetSourcePrefabName(GameObject go)
        {
            var src = PrefabUtility.GetCorrespondingObjectFromOriginalSource(go);
            if (src == null) return "";

            var path = AssetDatabase.GetAssetPath(src);
            if (string.IsNullOrEmpty(path)) return "";

            return Path.GetFileNameWithoutExtension(path);
        }

        // ============================================================
        //  点検
        // ============================================================
        private void BuildChecks()
        {
            _checks.Clear();
            if (target == null) return;

            // ---- 1. そもそも正しいものか ----
            if (EditorUtility.IsPersistent(target))
            {
                Add(false, true,
                    "プロジェクトの中のプレハブが入っています。ヒエラルキー（シーンに置いたほう）を入れてください。");
                return;
            }

            var allowed = false;
            foreach (var a in AllowedPrefabs) if (_sourcePrefab == a) allowed = true;

            var hasMachine = FindByTypeName(target, TypeMachine) != null;

            if (allowed)
            {
                Add(true, true, _sourcePrefab + " です。");
            }
            else if (string.IsNullOrEmpty(_sourcePrefab))
            {
                // プレハブから切り離されている場合は、中身で判断する
                if (hasMachine && _holders.Count > 0 && _darts.Count > 0)
                {
                    Add(true, true, "プレハブから切り離されていますが、中身はダーツ一式です。");
                }
                else
                {
                    Add(false, true, "ダーツ一式ではありません。CrecentDarts-PublicVariant を入れてください。");
                    return;
                }
            }
            else
            {
                Add(false, true,
                    "これは「" + _sourcePrefab + "」です。CrecentDarts-PublicVariant を入れてください。");
                return;
            }

            // ---- 2. 中身がそろっているか ----
            Add(_holders.Count > 0, true,
                _holders.Count > 0
                    ? "握るところ（ホルダー）が " + _holders.Count + " 個 見つかりました。"
                    : "握るところ（ホルダー）が見つかりません。");

            Add(_darts.Count > 0, true,
                _darts.Count > 0
                    ? "ダーツが " + _darts.Count + " 本 見つかりました。"
                    : "ダーツが見つかりません。");

            if (_holders.Count == 0 || _darts.Count == 0) return;

            // ---- 3. 握れるか ----
            var noPickup = 0;
            foreach (var h in _holders) if (!h.hasPickup) noPickup++;
            Add(noPickup == 0, false,
                noPickup == 0
                    ? "ホルダーは握れる状態です。"
                    : noPickup + " 個のホルダーが握れません。握らないと柄が反映されません。");

            // ---- 4. ホルダーとダーツの数が合っているか ----
            var listed = 0;
            foreach (var h in _holders) listed += h.dartCount;
            Add(listed == _darts.Count, false,
                listed == _darts.Count
                    ? "ホルダーとダーツの数が合っています。"
                    : "ホルダーが持つダーツは " + listed + " 本ですが、見つかったのは " + _darts.Count +
                      " 本です。ホルダーに登録されていないダーツには柄が乗りません。");

            // ---- 5. 塗る相手がそろっているか ----
            var missing = 0;
            foreach (var d in _darts) if (!d.AllPainted) missing++;
            Add(missing == 0, true,
                missing == 0
                    ? "全てのダーツで、塗る相手が見つかっています。"
                    : missing + " 本のダーツに、塗る相手が見つからない部分があります。下の一覧の 0 を確認してください。");

            // ---- 6. 形の入れ物に空欄が無いか ----
            var nulls = 0;
            foreach (var d in _darts) nulls += d.nullVariants;
            Add(nulls == 0, false,
                nulls == 0
                    ? "形の入れ物に空欄はありません。"
                    : "形の入れ物に空欄が " + nulls + " 個あります。その形に変えたとき、柄が出ません。");

            // ---- 7. 壊れたスクリプトが無いか ----
            var broken = 0;
            foreach (var d in _darts) if (d.missingScript) broken++;
            foreach (var h in _holders) if (HasMissingScript(h.go)) broken++;
            Add(broken == 0, true,
                broken == 0
                    ? "壊れたスクリプトはありません。"
                    : broken + " 個のオブジェクトに壊れたスクリプトがあります。先に直してください。");

            // ---- 8. ダーツ同士で描画部品を共有していないか ----
            //
            // 共有していると、1本の柄を変えたときに3本とも変わってしまう。
            // プレハブを複製して作った場合に起きやすい。
            var shared = FindSharedRenderers();
            Add(shared == 0, true,
                shared == 0
                    ? "ダーツはそれぞれ別の見た目を持っています。"
                    : shared + " 個の描画部品が複数のダーツで共有されています。" +
                      "このままだと1本変えると全部変わります。");

            // ---- 9. 柄が乗せられるマテリアルか ----
            //
            // 模様は _MainTex という場所に入れる。
            // そこが無いシェーダーだと、何をしても柄は出ない。
            var noSlot = CountRenderersWithoutMainTex();
            Add(noSlot == 0, false,
                noSlot == 0
                    ? "全ての部品に柄を入れる場所（_MainTex）があります。"
                    : noSlot + " 個の部品に柄を入れる場所がありません。そこだけ柄が出ません。");

            // ---- 10. ホルダーが見ているダーツが、この中にいるか ----
            var outside = CountDartsOutsideTarget();
            Add(outside == 0, false,
                outside == 0
                    ? "ホルダーは、この中のダーツを見ています。"
                    : "ホルダーが、入れたオブジェクトの外にあるダーツを " + outside +
                      " 本 見ています。そちらには柄が乗りません。");

            // ---- 5b. 付いているのに繋がっていないものが無いか ----
            //
            // 「MyDarts → セットアップ → 作り直す」を後から押すと、
            // 参照先が作り直されて、ここだけ切れた状態になる。
            // 部品は付いているので見落としやすい。
            var unwired = 0;
            foreach (var h in _holders) if (h.hook != null && !h.wired) unwired++;
            Add(unwired == 0, true,
                unwired == 0
                    ? "取り付け済みのものは、参照も生きています。"
                    : unwired + " 個のホルダーで参照が切れています。" +
                      "「取り付ける / 繋ぎ直す」を押してください。");

            // ---- 6. こちらの土台 ----
            Add(_fetcher != null, true,
                _fetcher != null
                    ? "自分の柄番号を持つ仕組みがシーンにあります。"
                    : "MyDartsFetcher がありません。先に「MyDarts → セットアップ」で一式を作ってください。");

            // ---- 7. 画像 ----
            Add(atlas != null, false,
                atlas != null
                    ? "柄の画像が入っています（" + atlas.width + " × " + atlas.height + "）。"
                    : "柄の画像がまだ入っていません。入れるまで柄は出ません。");

            var cells = gridX * gridY;
            Add(cells >= designCount, false,
                cells >= designCount
                    ? "マス数（" + gridX + "×" + gridY + "＝" + cells + "）が柄の数（" + designCount + "）を満たしています。"
                    : "マス数が足りません。" + gridX + "×" + gridY + "＝" + cells +
                      " に対して柄が " + designCount + " 個あります。");

            if (atlas != null)
            {
                var fitX = atlas.width % gridX == 0;
                var fitY = atlas.height % gridY == 0;
                Add(fitX && fitY, false,
                    fitX && fitY
                        ? "画像がマス数で割り切れます（1マス " + (atlas.width / gridX) + "px）。"
                        : "画像の大きさがマス数で割り切れません。柄の位置がずれます。");

                Add(atlas.width >= gridX * 128, false,
                    atlas.width >= gridX * 128
                        ? "1マスの大きさは足りています（" + (atlas.width / gridX) + "px）。"
                        : "1マスが " + (atlas.width / gridX) + "px しかありません。粗く見えます。");
            }
        }

        private void Add(bool ok, bool blocking, string text)
        {
            var c = new Check();
            c.ok = ok;
            c.blocking = blocking;
            c.text = text;
            _checks.Add(c);
        }

        /// <summary>2本以上のダーツで使い回されている描画部品の数</summary>
        private int FindSharedRenderers()
        {
            var seen = new Dictionary<Renderer, int>();

            for (var i = 0; i < _darts.Count; i++)
            {
                foreach (var r in _darts[i].AllRenderers())
                {
                    if (r == null) continue;
                    if (!seen.ContainsKey(r)) seen[r] = i;
                    else if (seen[r] != i) seen[r] = -1; // 別のダーツでも使われている
                }
            }

            var count = 0;
            foreach (var kv in seen) if (kv.Value == -1) count++;
            return count;
        }

        /// <summary>柄を入れる場所（_MainTex）が無い部品の数</summary>
        private int CountRenderersWithoutMainTex()
        {
            var bad = new List<Renderer>();

            foreach (var d in _darts)
            {
                foreach (var r in d.AllRenderers())
                {
                    if (r == null || bad.Contains(r)) continue;

                    var mats = r.sharedMaterials;
                    if (mats == null || mats.Length == 0) { bad.Add(r); continue; }

                    var ok = false;
                    for (var i = 0; i < mats.Length; i++)
                    {
                        if (mats[i] != null && mats[i].HasProperty("_MainTex")) ok = true;
                    }
                    if (!ok) bad.Add(r);
                }
            }
            return bad.Count;
        }

        /// <summary>ホルダーが見ているのに、入れたオブジェクトの外にいるダーツの数</summary>
        private int CountDartsOutsideTarget()
        {
            if (target == null) return 0;
            var count = 0;

            foreach (var h in _holders)
            {
                var so = new SerializedObject(h.crecent);
                count += CountOutside(so, FieldHolderDarts);
                count += CountOutside(so, FieldHolderRealDarts);
            }
            return count;
        }

        private int CountOutside(SerializedObject so, string field)
        {
            var p = so.FindProperty(field);
            if (p == null || !p.isArray) return 0;

            var count = 0;
            for (var i = 0; i < p.arraySize; i++)
            {
                var c = p.GetArrayElementAtIndex(i).objectReferenceValue as Component;
                if (c == null) continue;
                if (!c.transform.IsChildOf(target.transform)) count++;
            }
            return count;
        }

        /// <summary>配列の空欄の数</summary>
        private static int CountNulls(SerializedObject so, string field)
        {
            var p = so.FindProperty(field);
            if (p == null || !p.isArray) return 0;

            var count = 0;
            for (var i = 0; i < p.arraySize; i++)
            {
                if (p.GetArrayElementAtIndex(i).objectReferenceValue == null) count++;
            }
            return count;
        }

        /// <summary>参照が切れたスクリプトが付いていないか</summary>
        private static bool HasMissingScript(GameObject go)
        {
            var all = go.GetComponentsInChildren<Component>(true);
            for (var i = 0; i < all.Length; i++)
            {
                if (all[i] == null) return true;
            }
            return false;
        }

        private static Component FindByTypeName(GameObject root, string typeName)
        {
            var all = root.GetComponentsInChildren<UdonSharpBehaviour>(true);
            foreach (var c in all)
            {
                if (c != null && c.GetType().Name == typeName) return c;
            }
            return null;
        }

        /// <summary>まとめ画面から呼ぶ</summary>
        public void FindCandidateFromScene() { FindCandidate(); }

        private void FindCandidate()
        {
            var all = Object.FindObjectsOfType<UdonSharpBehaviour>(true);
            foreach (var c in all)
            {
                if (c == null) continue;
                var n = c.GetType().Name;
                if (n != TypeHolder && n != TypeDart && n != TypeMachine) continue;

                target = SnapToPrefabRoot(c.gameObject);
                Selection.activeGameObject = target;
                EditorGUIUtility.PingObject(target);
                Scan();
                return;
            }

            EditorUtility.DisplayDialog("見つかりません",
                "シーンにダーツギミックが見つかりませんでした。", "OK");
        }

        // ============================================================
        //  取り付け
        // ============================================================
        private void Attach()
        {
            EnsureLibrary();
            var added = 0;

            // 書き込みは SerializedObject 1回にまとめる。
            //
            // 途中で UpdateProxy() を挟むと、そこまでに書いた参照が
            // Udon側の古い値で巻き戻される。参照だけ消えて配列は残る、
            // という分かりにくい壊れ方をするので、混ぜてはいけない。
            foreach (var d in _darts)
            {
                var skin = d.go.GetComponent<MyDartsSkin>();
                if (skin == null)
                {
                    skin = d.go.AddUdonSharpComponent<MyDartsSkin>();
                    added++;
                }

                var so = new SerializedObject(d.crecent);
                var sso = new SerializedObject(skin);

                SetObj(sso, "library", _library);
                SetObjArray(sso, "tipRenderers", CollectRenderers(so, FieldTipRenderers));
                SetObjArray(sso, "barrelRenderers", CollectFromVariants(so, FieldBarrelVariants));
                SetObjArray(sso, "shaftRenderers", CollectFromVariants(so, FieldShaftVariants));
                SetObjArray(sso, "flightRenderers", CollectFromVariants(so, FieldFlightVariants));

                sso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(skin);
                EditorUtility.SetDirty(d.go);
            }

            foreach (var h in _holders)
            {
                var hook = h.go.GetComponent<MyDartsHolderHook>();
                if (hook == null)
                {
                    hook = h.go.AddUdonSharpComponent<MyDartsHolderHook>();
                    added++;
                }

                var so = new SerializedObject(h.crecent);

                var skins = new List<MyDartsSkin>();
                CollectSkins(so, FieldHolderDarts, skins);
                CollectSkins(so, FieldHolderRealDarts, skins);

                var hso = new SerializedObject(hook);

                SetObj(hso, "fetcher", _fetcher);
                SetObj(hso, "pickup", h.go.GetComponent(typeof(VRC_Pickup)));
                SetObj(hso, "debugPanel", Object.FindObjectOfType<MyDartsDebugPanel>(true));
                SetObjArray(hso, "darts", skins.ToArray());

                hso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(hook);
                EditorUtility.SetDirty(h.go);
            }

            // 本当に入ったか確かめる。入っていなければここで気づける
            VerifyAttach();

            Scan();
            Debug.Log("[MyDarts] 柄の部品を取り付けました。新しく足したもの: " + added + " 個");
        }

        private void EnsureLibrary()
        {
            if (_library != null) return;

            var go = GameObject.Find(LibraryObjectName);
            if (go == null)
            {
                go = new GameObject(LibraryObjectName);
                Undo.RegisterCreatedObjectUndo(go, "Create MyDarts Skin Library");
            }

            _library = go.GetComponent<MyDartsSkinLibrary>();
            if (_library == null) _library = go.AddUdonSharpComponent<MyDartsSkinLibrary>();

            // この画面で決めた設定をそのまま渡す
            PushLibrary();
        }

        // ============================================================
        //  取り外し
        // ============================================================
        private void Detach()
        {
            var removed = 0;

            foreach (var d in _darts)
            {
                if (d.go != null) removed += DestroyUdon(d.go.GetComponent<MyDartsSkin>());
            }
            foreach (var h in _holders)
            {
                if (h.go != null) removed += DestroyUdon(h.go.GetComponent<MyDartsHolderHook>());
            }

            // 消したぶんのネットワーク記録も一緒に片づける
            CleanNetworkIDsQuiet();

            Scan();
            Debug.Log("[MyDarts] 柄の部品を外しました: " + removed + " 個");
        }

        private static int DestroyUdon(UdonSharpBehaviour b)
        {
            if (b == null) return 0;

            var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(b);
            Undo.DestroyObjectImmediate(b);
            if (backing != null) Undo.DestroyObjectImmediate(backing);
            return 1;
        }

        // ============================================================
        //  見本のダーツを組み立てる
        //
        //  本物のダーツを複製して、
        //    掴む機能 / 当たり判定 / 物理 / 通信
        //  を全部剥がし、飾りだけを残す。
        //  投げられないし、他の人の画面には自分の選択が映る。
        // ============================================================
        private void CreatePreview()
        {
            if (_darts.Count == 0) return;

            EnsureLibrary();
            RemovePreview();

            var source = _darts[0];

            // ---- 複製して中身を剥がす ----
            var clone = Object.Instantiate(source.go);
            clone.name = "Dart";
            StripForDisplay(clone);

            // ---- 入れ物を作って、その中に置く ----
            var root = new GameObject(PreviewObjectName);
            Undo.RegisterCreatedObjectUndo(root, "Create MyDarts Preview");

            // 筐体のCanvasの横に置く。古い作りのシーンにも当たるよう両方見る
            var canvas = GameObject.Find("CabinetCanvas");
            if (canvas == null) canvas = GameObject.Find("CollectionCanvas");
            root.transform.position = (canvas != null ? canvas.transform.position : Vector3.zero)
                                      + previewOffset;

            clone.transform.SetParent(root.transform, false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one * previewScale;

            var preview = root.AddUdonSharpComponent<MyDartsPreviewDart>();

            // ---- 本物から、複製側の対応する部品を引き当てる ----
            var so = new SerializedObject(source.crecent);
            var src = source.go.transform;
            var dst = clone.transform;

            var pso = new SerializedObject(preview);
            SetObj(pso, "library", _library);
            SetObj(pso, "spin", root.transform);

            SetObjArray(pso, "barrelVariants", MapObjects(so, FieldBarrelVariants, src, dst));
            SetObjArray(pso, "flightVariants", MapObjects(so, FieldFlightVariants, src, dst));
            SetObjArray(pso, "shaftVariants", MapObjects(so, FieldShaftVariants, src, dst));

            SetObjArray(pso, "tipRenderers", MapRenderers(source.tipR, src, dst));
            SetObjArray(pso, "barrelRenderers", MapRenderers(source.barrelR, src, dst));
            SetObjArray(pso, "shaftRenderers", MapRenderers(source.shaftR, src, dst));
            SetObjArray(pso, "flightRenderers", MapRenderers(source.flightR, src, dst));

            pso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(preview);

            // ---- コレクション画面に繋ぐ ----
            var panel = Object.FindObjectOfType<MyDartsCollectionPanel>(true);
            if (panel != null)
            {
                var cso = new SerializedObject(panel);
                SetObj(cso, "preview", preview);
                cso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(panel);
            }
            else
            {
                Debug.LogWarning("[MyDarts] コレクション画面が見つかりませんでした。" +
                                 "見本は作られましたが、選択が映りません。");
            }

            Selection.activeGameObject = root;
            EditorGUIUtility.PingObject(root);
            Debug.Log("[MyDarts] 見本のダーツを作りました。位置と大きさはInspectorで調整できます。");
        }


        // ============================================================
        //  撮影セットのダーツ
        //
        //  ガチャ演出で見せる用。見本のダーツと同じ作り方だが、
        //  置き場所が「地面のはるか下の撮影セット」になる。
        //  人からは見えず、カメラを通してだけ映る。
        // ============================================================
        private void CreateStudio()
        {
            if (_darts.Count == 0) return;

            var studio = GameObject.Find("Studio");
            if (studio == null)
            {
                Debug.LogWarning("[MyDarts] 撮影セットが見つかりません。先に一式を生成してください。");
                return;
            }

            EnsureLibrary();

            var oldDart = GameObject.Find(StudioDartName);
            if (oldDart != null) Undo.DestroyObjectImmediate(oldDart);

            var source = _darts[0];

            var clone = Object.Instantiate(source.go);
            clone.name = "Dart";
            StripForDisplay(clone);

            var root = new GameObject(StudioDartName);
            Undo.RegisterCreatedObjectUndo(root, "Create MyDarts Studio Dart");

            root.transform.SetParent(studio.transform, false);
            root.transform.localPosition = Vector3.zero;

            clone.transform.SetParent(root.transform, false);
            clone.transform.localPosition = Vector3.zero;
            clone.transform.localRotation = Quaternion.identity;
            clone.transform.localScale = Vector3.one;

            var showcase = root.AddUdonSharpComponent<MyDartsPreviewDart>();

            var so = new SerializedObject(source.crecent);
            var src = source.go.transform;
            var dst = clone.transform;

            var pso = new SerializedObject(showcase);
            SetObj(pso, "library", _library);
            SetObj(pso, "spin", root.transform);

            SetObjArray(pso, "barrelVariants", MapObjects(so, FieldBarrelVariants, src, dst));
            SetObjArray(pso, "flightVariants", MapObjects(so, FieldFlightVariants, src, dst));
            SetObjArray(pso, "shaftVariants", MapObjects(so, FieldShaftVariants, src, dst));

            SetObjArray(pso, "tipRenderers", MapRenderers(source.tipR, src, dst));
            SetObjArray(pso, "barrelRenderers", MapRenderers(source.barrelR, src, dst));
            SetObjArray(pso, "shaftRenderers", MapRenderers(source.shaftR, src, dst));
            SetObjArray(pso, "flightRenderers", MapRenderers(source.flightR, src, dst));

            pso.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(showcase);

            // ---- ガチャ演出の進行役に繋ぐ ----
            var stage = Object.FindObjectOfType<MyDartsGachaStage>(true);
            if (stage != null)
            {
                var sso = new SerializedObject(stage);
                SetObj(sso, "showcase", showcase);
                sso.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(stage);
            }
            else
            {
                Debug.LogWarning("[MyDarts] ガチャ演出の進行役が見つかりませんでした。");
            }

            Debug.Log("[MyDarts] 撮影セットのダーツを作りました。");
        }

        private void RemovePreview()
        {
            var old = GameObject.Find(PreviewObjectName);
            if (old != null) Undo.DestroyObjectImmediate(old);
        }

        /// <summary>飾りにするために、動く仕掛けを全部外す</summary>
        private static void StripForDisplay(GameObject go)
        {
            // 順番が大事。ピックアップより先に物理を消すと外せなくなる
            foreach (var c in go.GetComponentsInChildren<UdonSharpBehaviour>(true))
            {
                if (c == null) continue;
                var backing = UdonSharpEditorUtility.GetBackingUdonBehaviour(c);
                Object.DestroyImmediate(c);
                if (backing != null) Object.DestroyImmediate(backing);
            }

            foreach (var c in go.GetComponentsInChildren<VRC.Udon.UdonBehaviour>(true))
            {
                if (c != null) Object.DestroyImmediate(c);
            }

            foreach (var c in go.GetComponentsInChildren(typeof(VRC_Pickup), true))
            {
                if (c != null) Object.DestroyImmediate(c);
            }

            foreach (var c in go.GetComponentsInChildren<Rigidbody>(true))
            {
                if (c != null) Object.DestroyImmediate(c);
            }

            foreach (var c in go.GetComponentsInChildren<Collider>(true))
            {
                if (c != null) Object.DestroyImmediate(c);
            }
        }

        /// <summary>本物の階層での位置を頼りに、複製側の同じものを引く</summary>
        private static Object[] MapObjects(SerializedObject so, string field, Transform src, Transform dst)
        {
            var list = new List<Object>();
            var p = so.FindProperty(field);
            if (p == null || !p.isArray) return list.ToArray();

            for (var i = 0; i < p.arraySize; i++)
            {
                var go = p.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                var t = MapTransform(go != null ? go.transform : null, src, dst);
                list.Add(t != null ? t.gameObject : null);
            }
            return list.ToArray();
        }

        private static Object[] MapRenderers(Renderer[] originals, Transform src, Transform dst)
        {
            var list = new List<Object>();
            if (originals == null) return list.ToArray();

            foreach (var r in originals)
            {
                var t = MapTransform(r != null ? r.transform : null, src, dst);
                list.Add(t != null ? t.GetComponent<Renderer>() : null);
            }
            return list.ToArray();
        }

        private static Transform MapTransform(Transform original, Transform src, Transform dst)
        {
            if (original == null) return null;
            if (original == src) return dst;

            var path = "";
            var t = original;
            while (t != null && t != src)
            {
                path = string.IsNullOrEmpty(path) ? t.name : t.name + "/" + path;
                t = t.parent;
            }
            if (t == null) return null; // 本物の外にあった

            return string.IsNullOrEmpty(path) ? dst : dst.Find(path);
        }

        // ============================================================
        //  柄の試し塗り
        //
        //  再生中と同じやり方（描画部品ごとの上書き）で、
        //  エディタ上でもその場で柄を乗せる。
        //  仕組みが動くかどうかを、握らずに確かめられる。
        // ============================================================
        private void PreviewPaint(int design)
        {
            if (atlas == null) return;

            var st = TextureST(design);
            var mpb = new MaterialPropertyBlock();
            var painted = 0;

            foreach (var d in _darts)
            {
                var whiten = design >= colorableBelow;
                painted += PaintSet(d.tipR, mpb, st, whiten);
                painted += PaintSet(d.barrelR, mpb, st, whitenBarrelTint || whiten);
                painted += PaintSet(d.shaftR, mpb, st, whiten);
                painted += PaintSet(d.flightR, mpb, st, whiten);
            }

            SceneView.RepaintAll();
            Debug.Log("[MyDarts] 柄 " + design + " を " + painted + " 個の部品に試し塗りしました。");
        }

        private int PaintSet(Renderer[] targets, MaterialPropertyBlock mpb, Vector4 st, bool whiten)
        {
            if (targets == null) return 0;
            var n = 0;

            foreach (var r in targets)
            {
                if (r == null) continue;

                r.GetPropertyBlock(mpb);
                mpb.SetTexture("_MainTex", atlas);
                mpb.SetVector("_MainTex_ST", st);
                if (whiten) mpb.SetColor("_Color", Color.white);
                r.SetPropertyBlock(mpb);
                n++;
            }
            return n;
        }

        private void PreviewClear()
        {
            var mpb = new MaterialPropertyBlock();
            foreach (var d in _darts)
            {
                foreach (var r in d.AllRenderers())
                {
                    if (r != null) r.SetPropertyBlock(null);
                }
            }
            SceneView.RepaintAll();
            Debug.Log("[MyDarts] 試し塗りを消しました。");
        }

        /// <summary>柄番号を _MainTex_ST に直す。ランタイム側と同じ計算</summary>
        private Vector4 TextureST(int design)
        {
            var gx = Mathf.Max(1, gridX);
            var gy = Mathf.Max(1, gridY);

            if (design < 0) design = 0;
            var col = design % gx;
            var row = design / gx;
            if (row >= gy) row = gy - 1;

            var cw = 1f / gx;
            var ch = 1f / gy;

            return new Vector4(
                cw - inset * 2f,
                ch - inset * 2f,
                col * cw + inset,
                1f - (row + 1) * ch + inset);
        }

        // ============================================================
        //  ネットワークIDの掃除
        //
        //  VRChatは「どのオブジェクトが何番の通信枠を使うか」をシーンに記録する。
        //  スクリプトを消しても記録は残るので、
        //  次に再生したとき「記録にあるのに実物が無い」と言われて止まる。
        //
        //    if (!pair.gameObject) → InvalidObject
        //
        //  消すたびにここで掃除しておけば、その状態にならない。
        // ============================================================

        /// <summary>まとめ画面の「開発」から呼ばれる</summary>
        public static void CleanNetworkIDs()
        {
            var removed = CleanNetworkIDsQuiet();

            EditorUtility.DisplayDialog(
                "ネットワークIDの掃除",
                removed > 0
                    ? "消えたスクリプトの記録を " + removed + " 件 掃除しました。\n" +
                      "シーンを保存してください（Ctrl+S）。"
                    : "掃除するものはありませんでした。",
                "OK");
        }

        /// <summary>掃除して、消した件数を返す。画面は出さない</summary>
        public static int CleanNetworkIDsQuiet()
        {
            var descriptor = Object.FindObjectOfType<VRC_SceneDescriptor>(true);
            if (descriptor == null) return 0;
            if (descriptor.NetworkIDCollection == null) return 0;

            var before = descriptor.NetworkIDCollection.Count;
            descriptor.NetworkIDCollection.RemoveAll(p => p == null || p.gameObject == null);
            var removed = before - descriptor.NetworkIDCollection.Count;

            if (removed <= 0) return 0;

            EditorUtility.SetDirty(descriptor);
            PrefabUtility.RecordPrefabInstancePropertyModifications(descriptor);
            EditorSceneManager.MarkSceneDirty(descriptor.gameObject.scene);

            Debug.Log("[MyDarts] ネットワークIDの記録を " + removed + " 件 掃除しました。");
            return removed;
        }

        // ============================================================
        //  小物
        // ============================================================
        private static int CountArray(SerializedObject so, string field)
        {
            var p = so.FindProperty(field);
            return (p != null && p.isArray) ? p.arraySize : 0;
        }

        private static Renderer[] CollectRenderers(SerializedObject so, string field)
        {
            var list = new List<Renderer>();
            var p = so.FindProperty(field);
            if (p == null || !p.isArray) return list.ToArray();

            for (var i = 0; i < p.arraySize; i++)
            {
                var r = p.GetArrayElementAtIndex(i).objectReferenceValue as Renderer;
                if (r != null && !list.Contains(r)) list.Add(r);
            }
            return list.ToArray();
        }

        /// <summary>
        /// 形の種類ごとに分かれた入れ物から、中の描画部品を全部集める。
        /// 出ていない形のものも塗っておく。あとで形を変えても柄が残るように。
        /// </summary>
        private static Renderer[] CollectFromVariants(SerializedObject so, string field)
        {
            var list = new List<Renderer>();
            var p = so.FindProperty(field);
            if (p == null || !p.isArray) return list.ToArray();

            for (var i = 0; i < p.arraySize; i++)
            {
                var go = p.GetArrayElementAtIndex(i).objectReferenceValue as GameObject;
                if (go == null) continue;

                var rs = go.GetComponentsInChildren<Renderer>(true);
                for (var j = 0; j < rs.Length; j++)
                {
                    if (rs[j] != null && !list.Contains(rs[j])) list.Add(rs[j]);
                }
            }
            return list.ToArray();
        }

        private static void CollectSkins(SerializedObject so, string field, List<MyDartsSkin> into)
        {
            var p = so.FindProperty(field);
            if (p == null || !p.isArray) return;

            for (var i = 0; i < p.arraySize; i++)
            {
                var c = p.GetArrayElementAtIndex(i).objectReferenceValue as Component;
                if (c == null) continue;

                var skin = c.GetComponent<MyDartsSkin>();
                if (skin != null && !into.Contains(skin)) into.Add(skin);
            }
        }

        private static void SetObj(SerializedObject so, string name, Object value)
        {
            var p = so.FindProperty(name);
            if (p == null)
            {
                Debug.LogWarning("[MyDarts] 項目が見つかりません: " + name);
                return;
            }
            p.objectReferenceValue = value;
        }

        /// <summary>参照の配列を入れる。SerializedObject の中だけで完結させる</summary>
        private static void SetObjArray(SerializedObject so, string name, Object[] values)
        {
            var p = so.FindProperty(name);
            if (p == null || !p.isArray)
            {
                Debug.LogWarning("[MyDarts] 配列の項目が見つかりません: " + name);
                return;
            }

            p.arraySize = values == null ? 0 : values.Length;
            for (var i = 0; i < p.arraySize; i++)
            {
                p.GetArrayElementAtIndex(i).objectReferenceValue = values[i];
            }
        }

        /// <summary>取り付けたあと、本当に入っているかを見て回る</summary>
        private void VerifyAttach()
        {
            var ng = 0;

            foreach (var d in _darts)
            {
                var skin = d.go.GetComponent<MyDartsSkin>();
                if (skin == null) { ng++; continue; }

                var so = new SerializedObject(skin);
                if (so.FindProperty("library").objectReferenceValue == null)
                {
                    Debug.LogError("[MyDarts] " + d.go.name + " に柄の入れ物が入りませんでした。");
                    ng++;
                }
            }

            foreach (var h in _holders)
            {
                var hook = h.go.GetComponent<MyDartsHolderHook>();
                if (hook == null) { ng++; continue; }

                var so = new SerializedObject(hook);
                if (so.FindProperty("fetcher").objectReferenceValue == null)
                {
                    Debug.LogError("[MyDarts] " + h.go.name + " に柄の出どころが入りませんでした。");
                    ng++;
                }
                if (so.FindProperty("darts").arraySize == 0)
                {
                    Debug.LogError("[MyDarts] " + h.go.name + " にダーツが入りませんでした。");
                    ng++;
                }
            }

            if (ng == 0) Debug.Log("[MyDarts] 参照の確認: すべて入っています。");
        }
    }
}
#endif
