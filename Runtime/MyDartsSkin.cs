using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

namespace MyDartsSystem
{
    /// <summary>
    /// ダーツ1本ぶんの柄。ダーツのオブジェクトに足して使う。
    ///
    /// ダーツギミック本体のスクリプトには触らない。
    /// あちらは形（どの部品を出すか）と色を MaterialPropertyBlock で扱っていて、
    /// こちらは同じ MaterialPropertyBlock の別の項目（テクスチャとUV）だけを書く。
    /// どちらも「読んでから書き足す」やり方なので、お互いを消さない。
    ///
    /// 柄番号は4パーツぶんを int 1つに詰めて同期する。
    /// 他の人の画面でも同じ柄で見える。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
    public class MyDartsSkin : UdonSharpBehaviour
    {
        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("柄のアトラスを持っているもの")]
        private MyDartsSkinLibrary library;

        [Header("=== 塗る相手 ===")]
        [Tooltip("形の種類ごとに分かれているので、出ていないものも含めて全部入れる")]
        [SerializeField] private Renderer[] tipRenderers;
        [SerializeField] private Renderer[] barrelRenderers;
        [SerializeField] private Renderer[] shaftRenderers;
        [SerializeField] private Renderer[] flightRenderers;

        /// <summary>
        /// 4パーツの柄番号を詰めたもの。
        /// 下位から チップ / バレル / シャフト / フライト の順に1バイトずつ。
        /// 柄は0〜255までなので1バイトに収まる。
        /// </summary>
        [UdonSynced] private int syncedDesigns;

        private MaterialPropertyBlock _mpb;
        private int _shown = -1;

        private void Start()
        {
            // 台帳の読み込みより先に動くことがあるので、少し待ってから一度塗る
            SendCustomEventDelayedSeconds(nameof(ApplyVisuals), 1f);
        }

        // ============================================================
        //  外から呼ぶ
        // ============================================================

        /// <summary>柄を決めて、他の人にも配る。オーナーだけが呼べる</summary>
        public void SetDesigns(int tip, int barrel, int shaft, int flight)
        {
            if (!Networking.IsOwner(gameObject)) return;

            var packed = Pack(tip, barrel, shaft, flight);
            if (packed != syncedDesigns)
            {
                syncedDesigns = packed;
                RequestSerialization();
            }

            ApplyVisuals();

            // ダーツギミック側は、握られたあと数フレームかけて色を塗り直す。
            // その後にもう一度塗っておかないと、白に戻したはずの色が上書きされる。
            SendCustomEventDelayedSeconds(nameof(ApplyVisuals), 1.5f);
        }

        public override void OnDeserialization()
        {
            ApplyVisuals();
        }

        /// <summary>
        /// いまの柄番号を見た目に反映する。
        /// 形を切り替えると出ている部品が変わるので、外から呼び直せるようにしてある。
        /// </summary>
        public void ApplyVisuals()
        {
            if (library == null)
            {
                Debug.Log("[MyDarts] skin: 柄の入れ物が繋がっていません（" + gameObject.name + "）");
                return;
            }
            if (!library.IsReady())
            {
                Debug.Log("[MyDarts] skin: 柄の画像が入っていません（" + gameObject.name + "）");
                return;
            }

            var atlas = library.GetAtlas();

            var tip = UnpackTip(syncedDesigns);
            var barrel = UnpackBarrel(syncedDesigns);
            var shaft = UnpackShaft(syncedDesigns);
            var flight = UnpackFlight(syncedDesigns);

            // 絵柄のときは色を白に戻す。掛け算で濁るのを避けるため。
            // 無地の素材のときは、プレイヤーが選んだ色をそのまま活かす。
            // バレルはもともと素材色が濃いので、常に白に戻す設定も残してある。
            Paint(tipRenderers, atlas, tip, !library.IsColorable(tip));
            Paint(barrelRenderers, atlas, barrel,
                  library.GetWhitenBarrelTint() || !library.IsColorable(barrel));
            Paint(shaftRenderers, atlas, shaft, !library.IsColorable(shaft));
            Paint(flightRenderers, atlas, flight, !library.IsColorable(flight));

            _shown = syncedDesigns;
        }

        public int GetTip() { return UnpackTip(syncedDesigns); }
        public int GetBarrel() { return UnpackBarrel(syncedDesigns); }
        public int GetShaft() { return UnpackShaft(syncedDesigns); }
        public int GetFlight() { return UnpackFlight(syncedDesigns); }

        /// <summary>いま出ている見た目が最新か</summary>
        public bool IsUpToDate() { return _shown == syncedDesigns; }

        // ============================================================
        //  塗る
        // ============================================================
        private void Paint(Renderer[] targets, Texture2D atlas, int design, bool whiten)
        {
            if (targets == null || targets.Length == 0) return;
            if (_mpb == null) _mpb = new MaterialPropertyBlock();

            var st = library.GetTextureST(design);

            for (var i = 0; i < targets.Length; i++)
            {
                var r = targets[i];
                if (r == null) continue;

                // 先に今の中身を読む。ダーツギミック側が入れた色を消さないため
                r.GetPropertyBlock(_mpb);

                _mpb.SetTexture("_MainTex", atlas);
                _mpb.SetVector("_MainTex_ST", st);

                // バレルだけは素材の色が濃く入っていて柄が沈むので、白に戻す。
                // 色を扱っているのはフライト・シャフト・チップなので、そちらには触らない。
                if (whiten) _mpb.SetColor("_Color", Color.white);

                r.SetPropertyBlock(_mpb);
            }
        }

        // ============================================================
        //  詰める・取り出す
        // ============================================================
        private int Pack(int tip, int barrel, int shaft, int flight)
        {
            return (Clamp255(tip))
                 | (Clamp255(barrel) << 8)
                 | (Clamp255(shaft) << 16)
                 | (Clamp255(flight) << 24);
        }

        private int Clamp255(int v)
        {
            if (v < 0) return 0;
            if (v > 255) return 255;
            return v;
        }

        private int UnpackTip(int packed) { return packed & 0xFF; }
        private int UnpackBarrel(int packed) { return (packed >> 8) & 0xFF; }
        private int UnpackShaft(int packed) { return (packed >> 16) & 0xFF; }
        private int UnpackFlight(int packed) { return (packed >> 24) & 0xFF; }
    }
}
