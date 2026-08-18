using UdonSharp;
using UnityEngine;
using UnityEngine.UI;

namespace MyDartsSystem
{
    /// <summary>
    /// 指で触って押せるボタン。
    ///
    /// 当たり判定にコライダーは使わない。
    /// 指先の位置をこのボタンの座標系に置き直して、
    /// 「枠の中に入っているか」「面からどれくらい近いか」で判定する。
    ///
    /// コライダーを置かない理由は、ダーツや人の動きに干渉させたくないため。
    /// 触られたかどうかを調べるのは MyDartsTouchInput の役目で、
    /// こちらは押された時の見た目・音・行き先だけを持つ。
    ///
    /// 押した合図は SendCustomEvent で渡す。
    /// ボタンの UnityEvent（ポインタで押す方）と同じ相手・同じ名前を入れてあるので、
    /// 指で押してもポインタで押しても同じことが起きる。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsTouchButton : UdonSharpBehaviour
    {
        [Header("=== 押した時の行き先 ===")]
        [SerializeField, Tooltip("合図を送る相手")]
        private UdonSharpBehaviour target;

        [SerializeField, Tooltip("送る合図の名前")]
        private string eventName;

        [Header("=== 見た目 ===")]
        [SerializeField, Tooltip("色を変える絵。ふつうはボタン自身の Image")]
        private Image graphic;

        [SerializeField, Tooltip("動かす枠。ふつうはボタン自身の RectTransform")]
        private RectTransform body;

        [SerializeField, Tooltip("押した時にどれくらい縮むか")]
        private float pressedScale = 0.955f;

        [SerializeField, Tooltip("縮んで戻るまでの時間")]
        private float pressDuration = 0.16f;

        [SerializeField, Tooltip("押した瞬間の色。混ぜる割合は下で決める")]
        private Color flashColor = new Color(1f, 1f, 1f, 1f);

        [SerializeField, Range(0f, 1f), Tooltip("押した瞬間にどれくらい白くするか")]
        private float flashAmount = 0.55f;

        [Header("=== 音 ===")]
        [SerializeField, Tooltip("空でも動く。あとから足せる")]
        private AudioSource audioSource;

        [SerializeField] private AudioClip pressSound;

        // 押し込みの再生状態
        private Color _normalColor;
        private Vector3 _restScale;
        private float _elapsed;
        private bool _playing;
        private bool _ready;

        private void Start()
        {
            Prepare();
        }

        private void Prepare()
        {
            if (_ready) return;
            _ready = true;

            if (body == null) body = (RectTransform)transform;
            _restScale = body.localScale;
            if (_restScale == Vector3.zero) _restScale = Vector3.one;

            if (graphic != null) _normalColor = graphic.color;
        }

        private void Update()
        {
            if (!_playing) return;

            _elapsed += Time.deltaTime;

            var dur = Mathf.Max(0.01f, pressDuration);
            var t = Mathf.Clamp01(_elapsed / dur);

            // 前半で沈み、後半で戻る。戻り側をゆっくりにすると押した感じが出る
            float scale;
            if (t < 0.32f) scale = Mathf.Lerp(1f, pressedScale, Ease(t / 0.32f));
            else scale = Mathf.Lerp(pressedScale, 1f, Ease((t - 0.32f) / 0.68f));

            body.localScale = _restScale * scale;

            if (graphic != null)
            {
                var mix = flashAmount * (1f - t);
                graphic.color = Color.Lerp(_normalColor, flashColor, mix);
            }

            if (t >= 1f)
            {
                body.localScale = _restScale;
                if (graphic != null) graphic.color = _normalColor;
                _playing = false;
            }
        }

        private float Ease(float v)
        {
            var inv = 1f - Mathf.Clamp01(v);
            return 1f - inv * inv * inv;
        }

        // ============================================================
        //  MyDartsTouchInput から呼ばれる
        // ============================================================

        /// <summary>
        /// 指先がこのボタンの上にあるか。
        /// depth と pad はメートルで受け取り、この場の単位に直して比べる。
        /// </summary>
        public bool IsHit(Vector3 worldPoint, float depthMeters, float padMeters)
        {
            Prepare();

            if (body == null) return false;
            if (!gameObject.activeInHierarchy) return false;

            var scale = body.lossyScale;
            if (scale.x <= 0.00001f || scale.z <= 0.00001f) return false;

            var local = body.InverseTransformPoint(worldPoint);
            var r = body.rect;

            var padX = padMeters / scale.x;
            var padY = padMeters / Mathf.Max(0.00001f, scale.y);
            var depth = depthMeters / scale.z;

            if (local.x < r.xMin - padX || local.x > r.xMax + padX) return false;
            if (local.y < r.yMin - padY || local.y > r.yMax + padY) return false;

            return Mathf.Abs(local.z) <= depth;
        }

        /// <summary>指が入った。押した扱いにする</summary>
        public void Press()
        {
            Prepare();

            _elapsed = 0f;
            _playing = true;

            if (audioSource != null && pressSound != null) audioSource.PlayOneShot(pressSound);

            Fire();
        }

        /// <summary>ポインタで押された時にも同じ演出を出すため</summary>
        public void PlayFeedbackOnly()
        {
            Prepare();
            _elapsed = 0f;
            _playing = true;
            if (audioSource != null && pressSound != null) audioSource.PlayOneShot(pressSound);
        }

        private void Fire()
        {
            if (target == null) return;
            if (eventName == null || eventName.Length == 0) return;
            target.SendCustomEvent(eventName);
        }

        public string GetEventName() { return eventName; }
    }
}
