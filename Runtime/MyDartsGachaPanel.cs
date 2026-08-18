using UdonSharp;
using UnityEngine;
using TMPro;

namespace MyDartsSystem
{
    /// <summary>
    /// ガチャの画面。プレイヤーが実際に触るのはここ。
    ///
    /// 引く操作そのものは MyDartsSender が持っているので、
    /// この script は表示だけを担当する。
    ///   ・所持コイン
    ///   ・単発 / 4種セットの値段（スプレッドシートの価格表から来る）
    ///   ・直前に何が出たか
    ///   ・集めた数
    ///
    /// 値段もレア度もサーバー側の表を見ているので、
    /// バランス調整でワールドを焼き直す必要はない。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsGachaPanel : UdonSharpBehaviour
    {
        [Header("=== 参照 ===")]
        [SerializeField] private MyDartsSender sender;
        [SerializeField] private MyDartsFetcher fetcher;

        [Header("=== 表示 ===")]
        [SerializeField, Tooltip("所持コイン")]
        private TextMeshProUGUI coinText;

        [SerializeField, Tooltip("単発の値段")]
        private TextMeshProUGUI priceOneText;

        [SerializeField, Tooltip("4種セットの値段")]
        private TextMeshProUGUI priceSetText;

        [SerializeField, Tooltip("出たもののレア度")]
        private TextMeshProUGUI resultTitle;

        [SerializeField, Tooltip("出たものの詳細")]
        private TextMeshProUGUI resultBody;

        [SerializeField, Tooltip("集めた数")]
        private TextMeshProUGUI collectionText;

        [Header("=== 動作 ===")]
        [SerializeField, Range(0.2f, 3f), Tooltip("表示を更新する間隔")]
        private float tick = 0.5f;

        // 直前に表示した結果。変わったときだけ書き換える
        private int _shownDesign = -2;

        private void Start()
        {
            ShowIdle();
            SendCustomEventDelayedSeconds(nameof(Tick), 1f);
        }

        public void Tick()
        {
            Refresh();
            SendCustomEventDelayedSeconds(nameof(Tick), tick);
        }

        private void Refresh()
        {
            if (sender == null || fetcher == null) return;

            var coin = sender.GetCoin();

            if (coinText != null)
            {
                coinText.text = "Coins   " + coin;
            }

            ShowPrice(priceOneText, MyDartsSender.KindGachaOne, coin);
            ShowPrice(priceSetText, MyDartsSender.KindGachaSet, coin);

            if (collectionText != null)
            {
                collectionText.text =
                    "Collected   Tip " + fetcher.GetOwnedCount(1) +
                    "   Barrel " + fetcher.GetOwnedCount(2) +
                    "   Shaft " + fetcher.GetOwnedCount(3) +
                    "   Flight " + fetcher.GetOwnedCount(4);
            }

            // 結果が更新されたときだけ書き換える
            var design = sender.GetLastDesign();
            if (design != _shownDesign)
            {
                _shownDesign = design;
                ShowResult();
            }
        }

        private void ShowPrice(TextMeshProUGUI label, int kind, int coin)
        {
            if (label == null || fetcher == null) return;

            var price = fetcher.GetPrice(kind, 0);
            label.text = price.ToString();
            label.color = (price > coin)
                ? new Color(0.95f, 0.5f, 0.5f, 1f)
                : new Color(0.95f, 0.85f, 0.4f, 1f);
        }

        private void ShowIdle()
        {
            if (resultTitle != null)
            {
                resultTitle.text = "-";
                resultTitle.color = new Color(0.6f, 0.6f, 0.6f, 1f);
            }
            if (resultBody != null) resultBody.text = "Pull to get a new design.";
        }

        private void ShowResult()
        {
            if (sender == null) return;

            var design = sender.GetLastDesign();
            if (design < 0)
            {
                ShowIdle();
                return;
            }

            var tier = sender.GetLastTier();

            // 見出しは「BARREL   47」「FULL SET   87」。レア度で色が変わる
            if (resultTitle != null)
            {
                resultTitle.text = sender.GetLastResultLine();
                resultTitle.color = TierColor(tier);
            }

            // レア度はレア度の色、NEW／ダブりはそれぞれの色。
            // 1行に混ぜたいので色は文字ごとに指定する
            if (resultBody != null)
            {
                resultBody.text =
                    "<color=" + TierHex(tier) + ">" + TierName(tier) + "</color>" +
                    (sender.GetLastWasDup()
                        ? ""
                        : "      <color=#35C6FF><b>NEW!</b></color>");

                resultBody.color = Color.white;
            }
        }

        private string TierHex(int tier)
        {
            if (tier <= 0) return "#C8D4DD";
            if (tier == 1) return "#35C6FF";
            if (tier == 2) return "#4C7CFF";
            return "#FFFFFF";
        }

        private string TierName(int tier)
        {
            if (tier <= 0) return "COMMON";
            if (tier == 1) return "RARE";
            if (tier == 2) return "VERY RARE";
            return "ULTRA RARE";
        }

        private Color TierColor(int tier)
        {
            if (tier <= 0) return new Color(0.78f, 0.83f, 0.87f, 1f);
            if (tier == 1) return new Color(0.21f, 0.78f, 1f, 1f);
            if (tier == 2) return new Color(0.30f, 0.49f, 1f, 1f);
            return new Color(1f, 1f, 1f, 1f);
        }
    }
}
