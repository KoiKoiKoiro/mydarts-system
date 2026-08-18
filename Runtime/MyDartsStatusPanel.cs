using UdonSharp;
using UnityEngine;
using TMPro;

namespace MyDartsSystem
{
    /// <summary>
    /// プレイヤーに見せる状況パネル。
    /// 「登録してください」「登録が完了しました」など、結果だけを短く出す。
    /// 技術的な内部状態は MyDartsDebugPanel の方に出す。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsStatusPanel : UdonSharpBehaviour
    {
        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("見出し（大きい文字）")]
        private TextMeshProUGUI titleText;

        [SerializeField, Tooltip("本文（説明文）")]
        private TextMeshProUGUI bodyText;

        [Header("=== 色 ===")]
        [SerializeField] private Color normalColor = new Color(1f, 1f, 1f, 1f);
        [SerializeField] private Color successColor = new Color(0.35f, 1f, 0.45f, 1f);
        [SerializeField] private Color errorColor = new Color(1f, 0.45f, 0.4f, 1f);

        /// <summary>通常メッセージ</summary>
        public void ShowInfo(string title, string body)
        {
            Apply(title, body, normalColor);
        }

        /// <summary>成功メッセージ</summary>
        public void ShowSuccess(string title, string body)
        {
            Apply(title, body, successColor);
        }

        /// <summary>失敗メッセージ</summary>
        public void ShowError(string title, string body)
        {
            Apply(title, body, errorColor);
        }

        private void Apply(string title, string body, Color color)
        {
            if (titleText != null)
            {
                titleText.text = title;
                titleText.color = color;
            }
            if (bodyText != null)
            {
                bodyText.text = body;
            }
        }
    }
}
