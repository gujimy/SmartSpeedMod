using System;
using FairyGUI;
using UnityEngine;

namespace SmartSpeedMod
{
    /// <summary>
    /// 游戏内倍率 HUD（极简版）：只显示当前倍率文本（如 "1x" / "2.5x"），右下角，占很小空间。
    /// 用 GRoot.inst + GGraph/GTextField 画在游戏画面上层，不拦截输入（touchable=false）。
    /// </summary>
    internal sealed class SpeedHud
    {
        private const float Padding = 5f;
        private readonly string _name;
        private readonly int _sortingOrder;

        private GComponent _layer;
        private GGraph _bg;
        private GTextField _text;

        private int _lastFontSize = -1;
        private string _lastMain;
        private float _lastRootW;
        private float _lastRootH;
        private float _lastX;
        private float _lastY;

        internal SpeedHud(string name, int sortingOrder)
        {
            _name = name;
            _sortingOrder = sortingOrder;
        }

        internal void Clear()
        {
            Dispose(_text); _text = null;
            Dispose(_bg); _bg = null;
            Dispose(_layer); _layer = null;
            _lastFontSize = -1;
            _lastMain = null;
        }

        private static void Dispose(GObject obj)
        {
            if (obj != null && !obj.isDisposed)
                obj.Dispose();
        }

        internal void Hide()
        {
            if (_layer != null && !_layer.isDisposed)
                _layer.visible = false;
        }

        /// <summary>
        /// 渲染极倍率文本（单行）。
        /// text: 显示的文本（如 "1x"）
        /// accent: 配色 0=灰(关闭) 1=青(抉择恢复) 2=金(加速中)
        /// anchorX/anchorY: 距右下角的像素距离
        /// </summary>
        internal void Render(string text, int accent, float anchorX, float anchorY, int fontSize)
        {
            if (string.IsNullOrEmpty(text))
            {
                Hide();
                return;
            }

            GRoot root = GRoot.inst;
            if (root == null)
                return;

            EnsureLayer(root);
            EnsureElements();

            bool textChanged = text != _lastMain || accent != _lastAccent;
            bool rootChanged = Math.Abs(root.width - _lastRootW) > 0.5f || Math.Abs(root.height - _lastRootH) > 0.5f;
            bool anchorChanged = Math.Abs(anchorX - _lastX) > 0.5f || Math.Abs(anchorY - _lastY) > 0.5f;

            try
            {
                int size = Math.Max(10, Math.Min(64, fontSize));

                if (textChanged)
                {
                    _lastMain = text;
                    _lastAccent = accent;
                    TextFormat tf = _text.textFormat;
                    tf.size = size;
                    tf.bold = true;
                    tf.color = accent == 0
                        ? new Color(0.6f, 0.6f, 0.6f, 1f)
                        : (accent == 1 ? new Color(0.35f, 0.9f, 1f, 1f) : new Color(1f, 0.84f, 0.3f, 1f));
                    tf.outline = 2f;
                    tf.outlineColor = Color.black;
                    ApplyFont(ref tf);
                    _text.textFormat = tf;
                    _text.text = text;

                    // 正方形紧凑背景，最小约 26×26（字号更大时自动撑大）
                    float box = Math.Max(26f, Math.Max(_text.textWidth, _text.textHeight) + Padding * 2f);
                    _panelW = box;
                    _panelH = box;
                    _bg.DrawRect(_panelW, _panelH, 1,
                        new Color(1f, 1f, 1f, 0.25f),
                        new Color(0.04f, 0.05f, 0.09f, 0.7f));
                }

                if (textChanged || rootChanged || anchorChanged)
                {
                    _lastRootW = root.width;
                    _lastRootH = root.height;
                    _lastX = anchorX;
                    _lastY = anchorY;
                    // 右下角定位：距右边缘 anchorX，距底部 anchorY
                    float x = Math.Max(0f, Math.Min(root.width - _panelW, root.width - _panelW - anchorX));
                    float y = Math.Max(0f, Math.Min(root.height - _panelH, root.height - _panelH - anchorY));
                    _bg.SetXY(x, y);
                    // 文字在方块内水平+垂直居中
                    _text.SetXY(
                        x + (int)((_panelW - _text.textWidth) / 2f),
                        y + (int)((_panelH - _text.textHeight) / 2f));
                }

                if (!_layer.visible)
                    _layer.visible = true;
            }
            catch (Exception)
            {
                Hide();
            }
        }

        private int _lastAccent = -1;
        private float _panelW;
        private float _panelH;

        private static void ApplyFont(ref TextFormat tf)
        {
            try
            {
                if (!string.IsNullOrEmpty(UIConfig.defaultFont))
                    tf.font = UIConfig.defaultFont;
            }
            catch { }
        }

        private void EnsureLayer(GRoot root)
        {
            if (_layer == null || _layer.isDisposed || _layer.parent != root)
            {
                Clear();
                _layer = new GComponent
                {
                    name = _name,
                    touchable = false,
                    sortingOrder = _sortingOrder
                };
                root.AddChild(_layer);
            }
        }

        private void EnsureElements()
        {
            if (_bg == null || _bg.isDisposed)
            {
                _bg = new GGraph { touchable = false };
                _layer.AddChild(_bg);
            }
            if (_text == null || _text.isDisposed)
            {
                _text = new GTextField { touchable = false, autoSize = AutoSizeType.Both };
                _layer.AddChild(_text);
                _lastMain = null;
            }
        }
    }
}
