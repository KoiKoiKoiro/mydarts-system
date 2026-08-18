using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDKBase;

namespace MyDartsSystem
{
    /// <summary>
    /// 筐体をタッチパネルとして扱うための入力。ここは各自のローカルだけで動く。
    ///
    ///   VRの人      … 人差し指で画面に触れて押す
    ///   デスクトップ … 今まで通りポインタで押す
    ///
    /// VRの人はポインタを切る。切らないと指とポインタの二重押しになるため。
    /// ただしトップ画面だけは例外で、URL入力欄がポインタでしか触れないので戻す。
    ///
    /// 指の位置は VRCPlayerApi.GetBonePosition で取る。
    /// 当たり判定はボタン側に任せていて、コライダーは1つも置かない。
    /// ダーツや人の動きに余計なものを増やしたくないため。
    ///
    /// ※ ワールド内に出す文字列だけは、フォントの都合で英語にしてある。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsTouchInput : UdonSharpBehaviour
    {
        [Header("=== ボタン ===")]
        [SerializeField, Tooltip("筐体の中の押せるもの全部")]
        private MyDartsTouchButton[] buttons;

        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("筐体の位置。近くにいる時だけ調べるため")]
        private Transform cabinet;

        [SerializeField, Tooltip("ポインタの受け口。VRの人には切る")]
        private GraphicRaycaster raycaster;

        [SerializeField] private MyDartsDebugPanel debugPanel;

        [Header("=== 効き具合 ===")]
        [SerializeField, Range(1f, 8f), Tooltip("これより遠いと何も調べない（m）")]
        private float reach = 3f;

        [SerializeField, Range(0.005f, 0.08f), Tooltip("面からどれだけ近づいたら押したことにするか（m）")]
        private float touchDepth = 0.022f;

        [SerializeField, Range(0f, 0.03f), Tooltip("枠の外側の余裕（m）")]
        private float touchPad = 0.004f;

        [SerializeField, Range(0.01f, 0.15f), Tooltip("これだけ離れたら指が抜けた扱い（m）")]
        private float releaseDepth = 0.055f;

        [SerializeField, Range(0.05f, 1f), Tooltip("同じ指で続けて押せるまでの間隔（秒）")]
        private float cooldown = 0.35f;

        [Header("=== 指先の目印（任意）===")]
        [SerializeField] private GameObject leftTip;
        [SerializeField] private GameObject rightTip;

        private VRCPlayerApi _player;
        private bool _vr;
        private bool _ready;

        // 0:左 1:右
        private MyDartsTouchButton[] _held;
        private float[] _cool;

        private bool _pointerAllowed = true;

        private void Start()
        {
            _held = new MyDartsTouchButton[2];
            _cool = new float[2];

            _player = Networking.LocalPlayer;
            _vr = _player != null && _player.IsUserInVR();
            _ready = true;

            ApplyPointer();
            ShowTips(false, false);

            if (debugPanel != null)
            {
                debugPanel.Log("touch: " + (_vr ? "VR (finger)" : "desktop (pointer)")
                               + "  buttons=" + (buttons == null ? 0 : buttons.Length));
            }
        }

        private void Update()
        {
            if (!_ready || !_vr) return;
            if (_player == null || !_player.IsValid()) return;
            if (buttons == null || buttons.Length == 0) return;

            // 近くにいない時は指の位置すら見にいかない
            if (cabinet != null &&
                Vector3.Distance(_player.GetPosition(), cabinet.position) > reach)
            {
                Release(0);
                Release(1);
                ShowTips(false, false);
                return;
            }

            var left = _player.GetBonePosition(HumanBodyBones.LeftIndexDistal);
            var leftHand = _player.GetBonePosition(HumanBodyBones.LeftHand);
            var leftOk = Valid(left, leftHand);

            var right = _player.GetBonePosition(HumanBodyBones.RightIndexDistal);
            var rightHand = _player.GetBonePosition(HumanBodyBones.RightHand);
            var rightOk = Valid(right, rightHand);

            ShowTips(leftOk, rightOk);
            if (leftTip != null && leftOk) leftTip.transform.position = left;
            if (rightTip != null && rightOk) rightTip.transform.position = right;

            Hand(0, left, leftOk);
            Hand(1, right, rightOk);
        }

        /// <summary>
        /// 指の骨が取れているか。
        /// 取れていない時は原点が返るので、手の位置と重なっていたら無効とみなす。
        /// </summary>
        private bool Valid(Vector3 tip, Vector3 hand)
        {
            if (tip == Vector3.zero || hand == Vector3.zero) return false;
            return Vector3.Distance(tip, hand) > 0.001f;
        }

        private void Hand(int side, Vector3 tip, bool ok)
        {
            if (_cool[side] > 0f) _cool[side] -= Time.deltaTime;

            if (!ok)
            {
                Release(side);
                return;
            }

            // すでに触っているものがあるなら、抜けたかどうかだけ見る
            var held = _held[side];
            if (held != null)
            {
                if (!held.IsHit(tip, releaseDepth, touchPad)) _held[side] = null;
                return;
            }

            if (_cool[side] > 0f) return;

            for (var i = 0; i < buttons.Length; i++)
            {
                var b = buttons[i];
                if (b == null) continue;
                if (!b.IsHit(tip, touchDepth, touchPad)) continue;

                _held[side] = b;
                _cool[side] = cooldown;
                b.Press();
                return;
            }
        }

        private void Release(int side)
        {
            _held[side] = null;
        }

        private void ShowTips(bool left, bool right)
        {
            if (leftTip != null && leftTip.activeSelf != left) leftTip.SetActive(left);
            if (rightTip != null && rightTip.activeSelf != right) rightTip.SetActive(right);
        }

        // ============================================================
        //  画面側から呼ぶ
        // ============================================================

        /// <summary>
        /// ポインタを使ってよいかどうか。
        /// トップ画面（URL入力欄がある）だけは、VRでも戻す。
        /// </summary>
        public void SetPointerAllowed(bool allowed)
        {
            if (_pointerAllowed == allowed && _ready) return;
            _pointerAllowed = allowed;
            ApplyPointer();
        }

        private void ApplyPointer()
        {
            if (raycaster == null) return;

            var on = !_vr || _pointerAllowed;
            if (raycaster.enabled != on) raycaster.enabled = on;
        }

        public bool IsVR() { return _vr; }
    }
}

