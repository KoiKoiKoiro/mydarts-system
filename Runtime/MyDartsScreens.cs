using UdonSharp;
using UnityEngine;
using TMPro;

namespace MyDartsSystem
{
    /// <summary>
    /// 筐体の画面切り替え。ゲームセンターの機械のような画面遷移をする。
    ///
    ///   トップ
    ///     └ メニュー
    ///         ├ ガチャ
    ///         │   ├ ノーマル（単発 / 4種コンプリート）
    ///         │   └ ピックアップ
    ///         ├ マイダーツ
    ///         └ クレジット
    ///
    /// 画面はどれも同じ場所に重ねて置いてあり、
    /// 表示するものを1枚だけ残して他を隠している。
    ///
    /// 「戻る」は通ってきた順番を覚えていて、1つ前へ返す。
    /// 分岐が増えても、戻り先を1つずつ指定しなくてよい。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsScreens : UdonSharpBehaviour
    {
        // 画面の番号。並び順は pages と合わせる
        public const int PageTop = 0;
        public const int PageMenu = 1;
        public const int PageGacha = 2;
        public const int PageGachaNormal = 3;
        public const int PageGachaPickup = 4;
        public const int PageMyDarts = 5;
        public const int PageCredit = 6;

        [Header("=== 画面 ===")]
        [Tooltip("順番は トップ / メニュー / ガチャ / ノーマル / ピックアップ / マイダーツ / クレジット")]
        [SerializeField] private GameObject[] pages;

        [Header("=== トップ画面 ===")]
        [SerializeField, Tooltip("登録が済んでいる人に出すボタン")]
        private GameObject playButton;

        [SerializeField, Tooltip("まだ登録していない人に出す入れ物")]
        private GameObject registerGroup;

        [SerializeField, Tooltip("トップに出す一言")]
        private TextMeshProUGUI topHint;

        [Header("=== 参照 ===")]
        [SerializeField] private MyDartsFetcher fetcher;
        [SerializeField] private MyDartsSender sender;
        [SerializeField] private MyDartsDebugPanel debugPanel;

        [Header("=== 表示 ===")]
        [SerializeField, Tooltip("どの画面にも出す所持クレジット")]
        private TextMeshProUGUI creditText;

        [SerializeField, Tooltip("クレジット画面の大きい数字")]
        private TextMeshProUGUI creditBig;

        [Header("=== 切り替えの動き ===")]
        [SerializeField, Tooltip("指で触って操作する仕掛け。トップだけポインタに戻すため")]
        private MyDartsTouchInput touchInput;

        [SerializeField, Range(0f, 0.6f), Tooltip("滑る時間（秒）。0で動かさない")]
        private float slideTime = 0.24f;

        [SerializeField, Tooltip("縦に動かす。切ると横になる")]
        private bool slideVertical = true;

        // 通ってきた画面。戻るときに使う
        private int[] _history;
        private int _depth;
        private int _current = -1;

        private bool _wasFound;
        private bool _topReady;

        // 滑らせている最中の状態
        private RectTransform[] _rects;
        private int _outgoing = -1;
        private float _slide;
        private bool _sliding;
        private float _dir = 1f;
        private float _span = 1000f;

        private void Start()
        {
            _history = new int[16];
            _depth = 0;

            CacheRects();

            ShowPage(PageTop);
            SendCustomEventDelayedSeconds(nameof(Tick), 1f);
        }

        /// <summary>画面の枠を覚えておく。毎回探すと重いので最初に1度だけ</summary>
        private void CacheRects()
        {
            if (pages == null) return;

            _rects = new RectTransform[pages.Length];
            for (var i = 0; i < pages.Length; i++)
            {
                if (pages[i] == null) continue;

                var rt = (RectTransform)pages[i].transform;
                _rects[i] = rt;

                var span = slideVertical ? rt.rect.height : rt.rect.width;
                if (span > 1f) _span = span;
            }
        }

        /// <summary>動かす向き。縦なら上下、横なら左右</summary>
        private Vector2 Offset(float amount)
        {
            return slideVertical ? new Vector2(0f, amount) : new Vector2(amount, 0f);
        }

        /// <summary>
        /// 画面を滑らせる。縦長の筐体なので上下に動かしている。
        ///
        ///   進む … 今の画面が上へ抜けて、次が下から上がってくる
        ///   戻る … 今の画面が下へ抜けて、前のが上から下りてくる
        ///
        /// ゲームセンターの機械らしく、最後だけゆっくり止める。
        /// はみ出した部分は Pages の切り抜きで隠れる。
        /// </summary>
        private void Update()
        {
            if (!_sliding) return;

            _slide += Time.deltaTime / Mathf.Max(0.01f, slideTime);
            var t = Mathf.Clamp01(_slide);
            var k = 1f - (1f - t) * (1f - t) * (1f - t);

            var inRect = Rect(_current);
            if (inRect != null)
            {
                inRect.anchoredPosition = Offset(Mathf.Lerp(-_dir * _span, 0f, k));
            }

            var outRect = Rect(_outgoing);
            if (outRect != null)
            {
                outRect.anchoredPosition = Offset(Mathf.Lerp(0f, _dir * _span, k));
            }

            if (t < 1f) return;

            if (inRect != null) inRect.anchoredPosition = Vector2.zero;
            if (outRect != null)
            {
                outRect.anchoredPosition = Vector2.zero;
                if (pages[_outgoing] != null) pages[_outgoing].SetActive(false);
            }

            _outgoing = -1;
            _sliding = false;
        }

        private RectTransform Rect(int index)
        {
            if (_rects == null) return null;
            if (index < 0 || index >= _rects.Length) return null;
            return _rects[index];
        }

        public void Tick()
        {
            SendCustomEventDelayedSeconds(nameof(Tick), 0.5f);
            RefreshTop();
            RefreshCredit();
        }

        // ============================================================
        //  画面の出し入れ
        // ============================================================

        /// <summary>指定した画面へ進む。来た道を覚えておく</summary>
        public void ShowPage(int index)
        {
            if (pages == null || index < 0 || index >= pages.Length) return;
            if (index == _current) return;

            if (_current >= 0 && _depth < _history.Length)
            {
                _history[_depth] = _current;
                _depth++;
            }

            Apply(index, 1f);
        }

        /// <summary>1つ前の画面へ返す</summary>
        public void Back()
        {
            if (_depth <= 0)
            {
                Apply(PageTop, -1f);
                return;
            }

            _depth--;
            Apply(_history[_depth], -1f);
        }

        /// <summary>トップまで一気に戻す</summary>
        public void Home()
        {
            _depth = 0;
            Apply(PageTop, -1f);
        }

        private void Apply(int index, float dir)
        {
            if (index == _current && _current >= 0) return;

            var from = _current;
            _current = index;

            // 動かしている途中にもう一度押された時のために、
            // 前の相手はここで畳んでおく
            if (_sliding && _outgoing >= 0 && _outgoing != index)
            {
                var prev = Rect(_outgoing);
                if (prev != null) prev.anchoredPosition = Vector2.zero;
                if (pages[_outgoing] != null) pages[_outgoing].SetActive(false);
            }

            for (var i = 0; i < pages.Length; i++)
            {
                if (pages[i] == null) continue;

                var show = (i == index) || (i == from && slideTime > 0.01f);
                if (pages[i].activeSelf != show) pages[i].SetActive(show);
            }

            if (slideTime > 0.01f && from >= 0)
            {
                _outgoing = from;
                _dir = dir;
                _slide = 0f;
                _sliding = true;

                var inRect = Rect(index);
                if (inRect != null) inRect.anchoredPosition = Offset(-dir * _span);
            }
            else
            {
                _outgoing = -1;
                _sliding = false;

                var inRect = Rect(index);
                if (inRect != null) inRect.anchoredPosition = Vector2.zero;
            }

            // トップだけポインタを戻す。URL入力欄がポインタでしか触れないため
            if (touchInput != null) touchInput.SetPointerAllowed(index == PageTop);

            if (debugPanel != null) debugPanel.Log("screen -> " + index);
        }

        // ============================================================
        //  ボタンから呼ぶ
        // ============================================================
        public void GoMenu() { ShowPage(PageMenu); }
        public void GoGacha() { ShowPage(PageGacha); }
        public void GoGachaNormal() { ShowPage(PageGachaNormal); }
        public void GoGachaPickup() { ShowPage(PageGachaPickup); }
        public void GoMyDarts() { ShowPage(PageMyDarts); }
        public void GoCredit() { ShowPage(PageCredit); }
        public void GoBack() { Back(); }
        public void GoHome() { Home(); }

        // ============================================================
        //  表示の更新
        // ============================================================

        /// <summary>
        /// 登録が済んでいるかでトップの中身を入れ替える。
        /// 未登録の人に「プレイ」を押させると何も起きなくて迷うので、
        /// 先に登録だけを見せる。
        /// </summary>
        private void RefreshTop()
        {
            if (fetcher == null) return;

            var found = fetcher.IsFound();

            // 初回は必ず1度そろえる。
            // 「変わっていないから何もしない」だけにすると、
            // 生成直後の見た目のまま止まってしまう
            if (_topReady && found == _wasFound) return;
            _topReady = true;
            _wasFound = found;

            if (playButton != null && playButton.activeSelf != found)
            {
                playButton.SetActive(found);
            }
            if (registerGroup != null && registerGroup.activeSelf == found)
            {
                registerGroup.SetActive(!found);
            }

            if (topHint != null)
            {
                topHint.text = found
                    ? "Press START"
                    : "First time? Register below.";
            }
        }

        private void RefreshCredit()
        {
            if (sender == null) return;

            var coin = sender.GetCoin();

            if (creditText != null) creditText.text = "CREDIT  " + coin;
            if (creditBig != null) creditBig.text = coin.ToString();
        }
    }
}
