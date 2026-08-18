using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Persistence;

namespace MyDartsSystem
{
    /// <summary>
    /// 見せるためだけのダーツ。試着室の鏡のようなもの。
    ///
    /// 本物のダーツを複製して、掴む機能・当たり判定・通信を全部外してある。
    /// 投げられないし、他の人からも見えない。手元の飾りとして回っているだけ。
    ///
    /// 映すのは「いま選んでいる途中の組み合わせ」で、
    /// EQUIP を押す前に見た目を確かめられる。
    ///
    /// 形（バレル・フライト・シャフト）はダーツギミック側の設定を読む。
    /// あちらのスクリプトは参照せず、保存されている場所だけを見る。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsPreviewDart : UdonSharpBehaviour
    {
        [Header("=== 参照 ===")]
        [SerializeField] private MyDartsSkinLibrary library;

        [Header("=== 形の入れ物 ===")]
        [Tooltip("本物と同じ並び。0から順に切り替わる")]
        [SerializeField] private GameObject[] barrelVariants;
        [SerializeField] private GameObject[] flightVariants;
        [SerializeField] private GameObject[] shaftVariants;

        [Header("=== 塗る相手 ===")]
        [SerializeField] private Renderer[] tipRenderers;
        [SerializeField] private Renderer[] barrelRenderers;
        [SerializeField] private Renderer[] shaftRenderers;
        [SerializeField] private Renderer[] flightRenderers;

        [Header("=== 回転 ===")]
        [SerializeField, Tooltip("回す対象。空なら自分自身を回す")]
        private Transform spin;

        [SerializeField, Range(0f, 120f), Tooltip("1秒あたりの回転角度")]
        private float rotateSpeed = 25f;

        [Header("=== 見せる時（ガチャ演出）===")]
        [SerializeField, Tooltip("部位ごとの大きさ。順番は チップ / バレル / シャフト / フライト")]
        private float[] showcaseScale = new float[] { 7f, 3.5f, 4.5f, 4f };

        [SerializeField, Tooltip("部位ごとの回す速さ（1秒あたりの角度）")]
        private float[] showcaseSpin = new float[] { 70f, 50f, 60f, 55f };

        [SerializeField, Tooltip("部位ごとの回す向き。空なら縦軸で回す")]
        private Vector3[] showcaseAxis;

        [SerializeField, Tooltip("一本まるごと見せる時の大きさ")]
        private float showcaseSetScale = 2.6f;

        [SerializeField, Range(0.1f, 1.5f), Tooltip("ポンと出てくるまでの時間")]
        private float popTime = 0.45f;

        // ダーツギミック側が形を保存している場所。型は参照せず名前だけ借りる
        private const string KeyBarrel = "cd_myDart_barrel";
        private const string KeyFlight = "cd_myDart_flight";
        private const string KeyShaft = "cd_myDart_shaft";

        private MaterialPropertyBlock _mpb;

        private int _tip = -1, _barrel = -1, _shaft = -1, _flight = -1;
        private int _shapeB = -1, _shapeF = -1, _shapeS = -1;
        private Color _tint = Color.white;

        // 見せる時の状態
        private bool _showcase;
        private int _showPart;          // 1:チップ 2:バレル 3:シャフト 4:フライト 0:まるごと
        private Vector3 _restScale = Vector3.one;
        private float _targetScale = 1f;
        private float _spinSpeed = 25f;
        private Vector3 _spinAxis = Vector3.up;
        private bool _popping;
        private float _popT;

        private void Start()
        {
            if (spin == null) spin = transform;
            _restScale = spin.localScale;
            if (_restScale == Vector3.zero) _restScale = Vector3.one;

            _spinSpeed = rotateSpeed;

            SendCustomEventDelayedSeconds(nameof(TickShape), 1f);
        }

        private void Update()
        {
            if (spin == null) return;

            // ポンと出てくる動き。行き過ぎてから戻ると気持ちよく見える
            if (_popping)
            {
                _popT += Time.deltaTime / Mathf.Max(0.05f, popTime);
                var t = Mathf.Clamp01(_popT);
                var k = EaseOutBack(t);

                spin.localScale = _restScale * (_targetScale * k);

                if (t >= 1f)
                {
                    spin.localScale = _restScale * _targetScale;
                    _popping = false;
                }
            }

            var axis = _showcase ? _spinAxis : Vector3.up;
            var speed = _showcase ? _spinSpeed : rotateSpeed;

            spin.Rotate(axis * (speed * Time.deltaTime), Space.Self);
        }

        /// <summary>行き過ぎてから戻る動き</summary>
        private float EaseOutBack(float t)
        {
            var c1 = 1.70158f;
            var c3 = c1 + 1f;
            var u = t - 1f;
            return 1f + c3 * u * u * u + c1 * u * u;
        }

        /// <summary>形の設定はいつ変わるか分からないので、定期的に見にいく</summary>
        public void TickShape()
        {
            SendCustomEventDelayedSeconds(nameof(TickShape), 0.5f);

            var local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) return;

            var b = PlayerData.HasKey(local, KeyBarrel) ? PlayerData.GetInt(local, KeyBarrel) : 0;
            var f = PlayerData.HasKey(local, KeyFlight) ? PlayerData.GetInt(local, KeyFlight) : 2;
            var s = PlayerData.HasKey(local, KeyShaft) ? PlayerData.GetInt(local, KeyShaft) : 0;

            if (b == _shapeB && f == _shapeF && s == _shapeS) return;

            _shapeB = b;
            _shapeF = f;
            _shapeS = s;

            Switch(barrelVariants, b);
            Switch(flightVariants, f);
            Switch(shaftVariants, s);

            // 形が変わると出ている部品が変わるので、塗り直す
            Repaint();
        }

        /// <summary>いま選んでいる柄を映す</summary>
        public void Show(int tip, int barrel, int shaft, int flight)
        {
            if (tip == _tip && barrel == _barrel && shaft == _shaft && flight == _flight) return;

            _tip = tip;
            _barrel = barrel;
            _shaft = shaft;
            _flight = flight;

            Repaint();
        }

        /// <summary>色を映す</summary>
        public void SetTint(Color color)
        {
            _tint = color;
            Repaint();
        }

        private void Repaint()
        {
            if (library == null || !library.IsReady()) return;
            if (_tip < 0) return;

            var atlas = library.GetAtlas();

            Paint(tipRenderers, atlas, _tip);
            Paint(barrelRenderers, atlas, _barrel);
            Paint(shaftRenderers, atlas, _shaft);
            Paint(flightRenderers, atlas, _flight);
        }

        private void Paint(Renderer[] targets, Texture2D atlas, int design)
        {
            if (targets == null || targets.Length == 0) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            var st = library.GetTextureST(design);

            // 絵柄のときは色を乗せない。本物と同じ規則にそろえる
            var color = library.IsColorable(design) ? _tint : Color.white;

            for (var i = 0; i < targets.Length; i++)
            {
                var r = targets[i];
                if (r == null) continue;

                r.GetPropertyBlock(_mpb);
                _mpb.SetTexture("_MainTex", atlas);
                _mpb.SetVector("_MainTex_ST", st);
                _mpb.SetColor("_Color", color);
                r.SetPropertyBlock(_mpb);
            }
        }

        // ============================================================
        //  ガチャ演出で見せる
        // ============================================================

        /// <summary>
        /// 当たった部位を1つだけ大きく見せる。
        /// 1:チップ 2:バレル 3:シャフト 4:フライト
        /// </summary>
        public void ShowcasePart(int kind, int design)
        {
            _showcase = true;
            _showPart = kind;

            var i = kind - 1;
            _targetScale = Pick(showcaseScale, i, 4f);
            _spinSpeed = Pick(showcaseSpin, i, 60f);
            _spinAxis = PickAxis(i);

            ApplyVisible();
            ForceDesign(design, kind);
            Pop();
        }

        /// <summary>4部位そろいを1本まるごと見せる</summary>
        public void ShowcaseSet(int design)
        {
            _showcase = true;
            _showPart = 0;

            _targetScale = showcaseSetScale;
            _spinSpeed = Pick(showcaseSpin, 1, 50f);
            _spinAxis = Vector3.up;

            ApplyVisible();
            ForceDesign(design, 0);
            Pop();
        }

        /// <summary>見せ終わり。元の大きさに戻して隠す</summary>
        public void ShowcaseHide()
        {
            _showcase = false;
            _popping = false;

            if (spin != null) spin.localScale = _restScale;

            SetVisible(tipRenderers, true);
            SetVisible(barrelRenderers, true);
            SetVisible(shaftRenderers, true);
            SetVisible(flightRenderers, true);
        }

        private void Pop()
        {
            _popT = 0f;
            _popping = true;
            if (spin != null) spin.localScale = Vector3.zero;
        }

        /// <summary>見せる部位だけ出す</summary>
        private void ApplyVisible()
        {
            var all = (_showPart <= 0 || _showPart > 4);

            SetVisible(tipRenderers, all || _showPart == 1);
            SetVisible(barrelRenderers, all || _showPart == 2);
            SetVisible(shaftRenderers, all || _showPart == 3);
            SetVisible(flightRenderers, all || _showPart == 4);
        }

        private void SetVisible(Renderer[] targets, bool visible)
        {
            if (targets == null) return;

            for (var i = 0; i < targets.Length; i++)
            {
                if (targets[i] == null) continue;
                if (targets[i].enabled != visible) targets[i].enabled = visible;
            }
        }

        /// <summary>
        /// 柄を入れ直して塗る。同じ番号でも塗り直す。
        /// 出ていない部位も同じ番号にしておく。塗り分ける必要が無いのと、
        /// 一部だけ未設定のままだと塗る処理が動かないため。
        /// </summary>
        private void ForceDesign(int design, int kind)
        {
            _tip = design;
            _barrel = design;
            _shaft = design;
            _flight = design;

            Repaint();
        }

        private float Pick(float[] values, int index, float fallback)
        {
            if (values == null || index < 0 || index >= values.Length) return fallback;
            if (values[index] <= 0f) return fallback;
            return values[index];
        }

        private Vector3 PickAxis(int index)
        {
            if (showcaseAxis == null || index < 0 || index >= showcaseAxis.Length) return Vector3.up;
            if (showcaseAxis[index] == Vector3.zero) return Vector3.up;
            return showcaseAxis[index];
        }

        private void Switch(GameObject[] variants, int index)
        {
            if (variants == null || variants.Length == 0) return;

            if (index < 0) index = 0;
            if (index >= variants.Length) index = variants.Length - 1;

            for (var i = 0; i < variants.Length; i++)
            {
                if (variants[i] != null) variants[i].SetActive(i == index);
            }
        }
    }
}
