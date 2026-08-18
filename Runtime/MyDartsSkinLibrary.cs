using UdonSharp;
using UnityEngine;

namespace MyDartsSystem
{
    /// <summary>
    /// 柄のアトラスを持つ場所。シーンに1つ置く。
    ///
    /// 柄は1枚の大きな画像に格子状に並べてあり、
    /// 「柄番号」はその何マス目を見るか、という意味になる。
    /// マテリアルは増やさず、UVをずらすだけで切り替わる。
    ///
    /// 柄を増やすときは、アトラスの画像を差し替えて
    /// 縦横のマス数と柄の数を直すだけでよい。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsSkinLibrary : UdonSharpBehaviour
    {
        [Header("=== アトラス ===")]
        [SerializeField, Tooltip("柄を格子状に並べた画像。左上が柄0番で、右へ順に進む")]
        private Texture2D atlas;

        [SerializeField, Range(1, 16), Tooltip("横のマス数")]
        private int gridX = 8;

        [SerializeField, Range(1, 16), Tooltip("縦のマス数")]
        private int gridY = 8;

        [SerializeField, Range(1, 256), Tooltip("実際に使う柄の数。マス数より少なくてよい")]
        private int designCount = 64;

        [Header("=== 細かい調整 ===")]
        [SerializeField, Range(0f, 0.02f),
         Tooltip("マスの端を少し内側に寄せる量。隣のマスの色がにじむときに上げる")]
        private float inset = 0.002f;

        [SerializeField, Tooltip("バレルの色味を白に戻す。素材の色が乗って柄が沈むときに使う")]
        private bool whitenBarrelTint = true;

        [Header("=== 色を乗せる範囲 ===")]
        [Tooltip("この番号より前の柄は、プレイヤーが選んだ色が乗る。\n" +
                 "それ以降の柄は色を白に戻して、柄をそのまま見せる。\n" +
                 "無地の素材は色を変えられて、絵柄は絵柄のまま、という切り分け。")]
        [SerializeField, Range(0, 256)]
        private int colorableBelow = 40;

        public Texture2D GetAtlas() { return atlas; }
        public int GetDesignCount() { return designCount; }
        public bool GetWhitenBarrelTint() { return whitenBarrelTint; }
        public bool IsReady() { return atlas != null; }

        /// <summary>
        /// この柄はプレイヤーの色を乗せてよいか。
        ///
        /// 無地の素材（金属・木目・大理石など）は色が乗ったほうが選択肢が増える。
        /// 絵柄（桜・星雲など）は色が掛かると濁るので、白に戻して素のまま見せる。
        /// </summary>
        public bool IsColorable(int design)
        {
            return design < colorableBelow;
        }

        /// <summary>
        /// 柄番号を _MainTex_ST の形（横倍率, 縦倍率, 横位置, 縦位置）にする。
        ///
        /// UVの原点は左下だが、画像は左上から数えるほうが分かりやすいので、
        /// ここで上下をひっくり返している。
        /// </summary>
        public Vector4 GetTextureST(int design)
        {
            if (gridX < 1) gridX = 1;
            if (gridY < 1) gridY = 1;

            if (design < 0) design = 0;
            if (design >= designCount) design = design % designCount;

            var col = design % gridX;
            var row = design / gridX;
            if (row >= gridY) row = gridY - 1;

            var cellW = 1f / gridX;
            var cellH = 1f / gridY;

            return new Vector4(
                cellW - inset * 2f,
                cellH - inset * 2f,
                col * cellW + inset,
                1f - (row + 1) * cellH + inset);
        }
    }
}
