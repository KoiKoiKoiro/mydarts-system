using UdonSharp;
using UnityEngine;
using TMPro;

namespace MyDartsSystem
{
    /// <summary>
    /// パーツ変更の動作確認用。
    ///
    /// 4種類それぞれに番号を入れて、個別に送信できる。
    /// 所持していない番号なら代金が引かれ、所持済みなら無料で付け替わる。
    ///
    /// 本番のショップUIができたら、この階層ごと消してよい。
    /// 送信の呼び出し方（Begin に種別と値を渡すだけ）はショップでも同じ。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsPartsTester : UdonSharpBehaviour
    {
        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("送信の本体")]
        private MyDartsSender sender;

        [SerializeField, Tooltip("いまの装備を初期値に入れるのに使う")]
        private MyDartsFetcher fetcher;

        [SerializeField, Tooltip("開発用のログパネル")]
        private MyDartsDebugPanel debugPanel;

        [Header("=== 入力欄 ===")]
        [SerializeField] private TMP_InputField tipField;
        [SerializeField] private TMP_InputField barrelField;
        [SerializeField] private TMP_InputField shaftField;
        [SerializeField] private TMP_InputField flightField;
        [SerializeField, Tooltip("フライト形状。0〜3")] private TMP_InputField shapeField;

        [Header("=== 表示 ===")]
        [SerializeField, Tooltip("いまの装備を出す欄")]
        private TextMeshProUGUI currentText;

        [SerializeField, Tooltip("各行の値段を出す欄。順番は Tip/Barrel/Shaft/Flight/Shape")]
        private TextMeshProUGUI[] priceLabels;

        [SerializeField, Tooltip("カートの中身と合計を出す欄")]
        private TextMeshProUGUI cartText;

        private void Start()
        {
            // 台帳の読み込みが終わるころに初期値を入れる
            SendCustomEventDelayedSeconds(nameof(FillFromLedger), 6f);
            SendCustomEventDelayedSeconds(nameof(TickPrices), 1f);
        }

        /// <summary>
        /// 入力欄の番号に対する値段を出し続ける。
        /// 入力の変化を拾うイベントを繋ぐより、定期的に見にいくほうが単純で壊れにくい。
        /// </summary>
        public void TickPrices()
        {
            RefreshPrices();

            // 送信が終わるとカートは空になるので、表示も追従させる
            if (sender != null && sender.GetQueueCount() <= 0 && _cartKinds.Length > 0)
            {
                _cartKinds = "";
                _cartTotal = 0;
                Refresh();
            }
            RefreshCart();

            SendCustomEventDelayedSeconds(nameof(TickPrices), 0.5f);
        }

        private void RefreshPrices()
        {
            if (priceLabels == null || fetcher == null) return;

            ShowPrice(0, MyDartsSender.KindTip, tipField);
            ShowPrice(1, MyDartsSender.KindBarrel, barrelField);
            ShowPrice(2, MyDartsSender.KindShaft, shaftField);
            ShowPrice(3, MyDartsSender.KindFlight, flightField);
            ShowPrice(4, MyDartsSender.KindFlightShape, shapeField);
        }

        private void ShowPrice(int index, int kind, TMP_InputField field)
        {
            if (index < 0 || index >= priceLabels.Length) return;

            var label = priceLabels[index];
            if (label == null || field == null) return;

            var value = ParseInt(field.text, -1);
            if (value < 0)
            {
                label.text = "-";
                label.color = new Color(0.6f, 0.6f, 0.6f, 1f);
                return;
            }

            // 形状は所持の概念が無く、常に無料
            if (kind == MyDartsSender.KindFlightShape)
            {
                label.text = "free";
                label.color = new Color(0.5f, 0.85f, 0.6f, 1f);
                return;
            }

            if (fetcher.IsOwned(kind, value))
            {
                label.text = "OWNED";
                label.color = new Color(0.5f, 0.85f, 0.6f, 1f);
                return;
            }

            var price = fetcher.GetPrice(kind, value);
            label.text = price.ToString();

            // 買えないものは赤くしておく
            var coin = sender != null ? sender.GetCoin() : fetcher.GetMyCoin();
            label.color = (price > coin)
                ? new Color(0.95f, 0.5f, 0.5f, 1f)
                : new Color(0.95f, 0.85f, 0.4f, 1f);
        }

        /// <summary>いまの装備を入力欄に反映する</summary>
        public void FillFromLedger()
        {
            if (fetcher == null || !fetcher.IsFound())
            {
                SendCustomEventDelayedSeconds(nameof(FillFromLedger), 6f);
                return;
            }

            SetField(tipField, fetcher.GetTip());
            SetField(barrelField, fetcher.GetBarrel());
            SetField(shaftField, fetcher.GetShaft());
            SetField(flightField, fetcher.GetFlight());
            SetField(shapeField, fetcher.GetFlightShape());

            Refresh();
        }

        /// <summary>いまの装備を1行で出す</summary>
        public void Refresh()
        {
            if (currentText == null) return;
            if (fetcher == null) return;

            currentText.text =
                "Now:  Tip " + fetcher.GetTip() +
                "   Barrel " + fetcher.GetBarrel() +
                "   Shaft " + fetcher.GetShaft() +
                "   Flight " + fetcher.GetFlight() + "-" + fetcher.GetFlightShape();
        }

        // ============================================================
        //  ボタンから呼ぶ
        // ============================================================
        public void OnClickTip() { AddToCart(MyDartsSender.KindTip, tipField); }
        public void OnClickBarrel() { AddToCart(MyDartsSender.KindBarrel, barrelField); }
        public void OnClickShaft() { AddToCart(MyDartsSender.KindShaft, shaftField); }
        public void OnClickFlight() { AddToCart(MyDartsSender.KindFlight, flightField); }
        public void OnClickShape() { AddToCart(MyDartsSender.KindFlightShape, shapeField); }

        /// <summary>カートの中身をまとめて送る</summary>
        public void OnClickBuy()
        {
            if (sender == null) return;

            if (sender.GetQueueCount() <= 0)
            {
                if (debugPanel != null) debugPanel.Log("cart is empty");
                return;
            }

            if (debugPanel != null) debugPanel.Log("buy: " + sender.GetQueueCount() + " item(s)");
            sender.FlushQueue();
        }

        /// <summary>カートを空にする</summary>
        public void OnClickClear()
        {
            if (sender == null) return;
            sender.ClearQueue();
            _cartKinds = "";
            _cartTotal = 0;
            RefreshCart();
        }

        private void AddToCart(int kind, TMP_InputField field)
        {
            if (sender == null)
            {
                if (debugPanel != null) debugPanel.Log("tester: sender is not assigned");
                return;
            }

            if (field == null)
            {
                if (debugPanel != null) debugPanel.Log("tester: input field is not assigned");
                return;
            }

            var value = ParseInt(field.text, -1);

            if (value < 0)
            {
                if (debugPanel != null) debugPanel.Log("tester: enter a number");
                return;
            }

            // 形状だけ 0〜3。ほかは 0〜199
            var max = (kind == MyDartsSender.KindFlightShape) ? 3 : 199;
            if (value > max)
            {
                if (debugPanel != null) debugPanel.Log("tester: max is " + max);
                return;
            }

            var price = fetcher.GetPrice(kind, value);

            if (!sender.Enqueue(kind, value)) return;

            // 表示用に積み直す。同じ種別は上書きされるので合計も入れ直す
            RebuildCartSummary();

            if (debugPanel != null)
            {
                debugPanel.Log("cart += kind=" + kind + " value=" + value + " price=" + price);
            }
        }

        // ============================================================
        //  カートの表示
        // ============================================================
        private string _cartKinds = "";
        private int _cartTotal;

        private void RebuildCartSummary()
        {
            // Sender は中身を種別で持っているので、こちらは入力欄から作り直す
            _cartKinds = "";
            _cartTotal = 0;

            AddSummary(MyDartsSender.KindTip, tipField, "Tip");
            AddSummary(MyDartsSender.KindBarrel, barrelField, "Barrel");
            AddSummary(MyDartsSender.KindShaft, shaftField, "Shaft");
            AddSummary(MyDartsSender.KindFlight, flightField, "Flight");
            AddSummary(MyDartsSender.KindFlightShape, shapeField, "Shape");

            RefreshCart();
        }

        private void AddSummary(int kind, TMP_InputField field, string label)
        {
            if (sender == null || field == null) return;
            if (!sender.IsInQueue(kind)) return;

            var value = sender.QueuedValueOf(kind);
            var price = (kind == MyDartsSender.KindFlightShape) ? 0 : fetcher.GetPrice(kind, value);

            _cartTotal += price;
            if (_cartKinds.Length > 0) _cartKinds += ",  ";
            _cartKinds += label + " " + value + (price > 0 ? " (" + price + ")" : "");
        }

        private void RefreshCart()
        {
            if (cartText == null) return;

            if (sender == null || sender.GetQueueCount() <= 0)
            {
                cartText.text = "Cart: empty";
                cartText.color = new Color(0.6f, 0.6f, 0.6f, 1f);
                return;
            }

            var coin = sender.GetCoin();
            cartText.text = "Cart: " + _cartKinds + "   =  " + _cartTotal;
            cartText.color = (_cartTotal > coin)
                ? new Color(0.95f, 0.5f, 0.5f, 1f)
                : new Color(0.95f, 0.85f, 0.4f, 1f);
        }

        // ============================================================
        //  小物
        // ============================================================
        private void SetField(TMP_InputField field, int value)
        {
            if (field != null) field.text = value.ToString();
        }

        /// <summary>数字だけ拾う。Udonには int.TryParse が無い</summary>
        private int ParseInt(string src, int fallback)
        {
            if (string.IsNullOrEmpty(src)) return fallback;

            var n = 0;
            var any = false;

            for (var i = 0; i < src.Length; i++)
            {
                var c = src[i];
                if (c < '0' || c > '9') continue;
                n = n * 10 + (c - '0');
                any = true;
                if (n > 99999) break;
            }

            return any ? n : fallback;
        }
    }
}
