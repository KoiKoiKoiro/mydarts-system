using UdonSharp;
using UnityEngine;
using TMPro;

namespace MyDartsSystem
{
    /// <summary>
    /// 開発用のログパネル。プレイヤーには見せない場所に置く。
    /// 送信中・クールタイム・レスポンス内容など、内部状態をそのまま流す。
    /// 行数は maxLines で頭打ちにして、古いものから捨てる。
    /// </summary>
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class MyDartsDebugPanel : UdonSharpBehaviour
    {
        [Header("=== 参照 ===")]
        [SerializeField, Tooltip("ログを流すテキスト")]
        private TextMeshProUGUI logText;

        [SerializeField, Tooltip("現在の状態を1行で出すテキスト")]
        private TextMeshProUGUI stateText;

        [Header("=== 設定 ===")]
        [SerializeField, Range(5, 60), Tooltip("保持する行数")]
        private int maxLines = 20;

        [SerializeField, Tooltip("Unityのコンソールにも出す")]
        private bool alsoUnityLog = true;

        // UdonSharpではListが使えないので配列でリングバッファを組む
        private string[] _lines;
        private int _count;

        private void Start()
        {
            if (maxLines < 5) maxLines = 5;
            _lines = new string[maxLines];
            _count = 0;
            Redraw();
        }

        /// <summary>1行追加する</summary>
        public void Log(string line)
        {
            if (_lines == null)
            {
                if (maxLines < 5) maxLines = 5;
                _lines = new string[maxLines];
                _count = 0;
            }

            var stamped = "[" + TimeStamp() + "] " + line;

            if (_count < _lines.Length)
            {
                _lines[_count] = stamped;
                _count++;
            }
            else
            {
                // 1行ずつ前へ詰めて、最後に新しい行を入れる
                for (var i = 0; i < _lines.Length - 1; i++)
                {
                    _lines[i] = _lines[i + 1];
                }
                _lines[_lines.Length - 1] = stamped;
            }

            if (alsoUnityLog)
            {
                Debug.Log("[MyDarts] " + line);
            }

            Redraw();
        }

        /// <summary>いま何をしているかの1行表示（送信中 / 待機中 など）</summary>
        public void SetState(string state)
        {
            if (stateText != null)
            {
                stateText.text = state;
            }
        }

        /// <summary>ログを空にする</summary>
        public void Clear()
        {
            _count = 0;
            if (_lines != null)
            {
                for (var i = 0; i < _lines.Length; i++) _lines[i] = null;
            }
            Redraw();
        }

        private void Redraw()
        {
            if (logText == null) return;
            if (_lines == null)
            {
                logText.text = "";
                return;
            }

            var buffer = "";
            for (var i = 0; i < _lines.Length; i++)
            {
                if (string.IsNullOrEmpty(_lines[i])) continue;
                if (buffer.Length > 0) buffer += "\n";
                buffer += _lines[i];
            }
            logText.text = buffer;
        }

        private string TimeStamp()
        {
            var t = Time.timeSinceLevelLoad;
            var m = (int)(t / 60f);
            var s = (int)(t % 60f);
            return (m < 10 ? "0" : "") + m + ":" + (s < 10 ? "0" : "") + s;
        }
    }
}
