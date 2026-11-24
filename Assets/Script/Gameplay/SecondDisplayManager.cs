using System.Collections.Generic;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using TMPro;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using YARG.Core.IO;
using YARG.Core.Logging;
using YARG.Helpers;
using YARG.Localization;
using YARG.Settings;

namespace YARG.Venue
{
    /// <summary>
    /// Persistent second-monitor output: a waiting slideshow until a song video plays.
    /// </summary>
    public sealed class SecondDisplayManager : MonoSingleton<SecondDisplayManager>
    {
        public const int DisplayIndex = 1;

        private const float SlideshowInterval = 30f;
        private const float FadeDuration = 1f;

        // Upper bound for the uploaded texture; huge source images are downscaled off-thread.
        private const int MaxTextureWidth = 3840;
        private const int MaxTextureHeight = 2160;

        private static readonly string[] ImageExtensions =
        {
            "*.png", "*.jpg", "*.jpeg"
        };

        public static string WaitingFolder
        {
            get
            {
                var folder = Path.Combine(PathHelper.PersistentDataPath, "waiting");
                if (!Directory.Exists(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                return folder;
            }
        }

        public static bool IsReady => Instance != null && Instance._ready;

        private static int _mainThreadId;

        private CanvasGroup _waitingGroup;
        private CanvasGroup _videoGroup;
        private RawImage _imageA;
        private RawImage _imageB;
        private RawImage _videoImage;
        private TextMeshProUGUI _waitingText;

        private readonly List<string> _imagePaths = new();
        private Texture2D _textureA;
        private Texture2D _textureB;
        private bool _showingA = true;
        private bool _showingVideo;
        private bool _ready;
        private bool _fading;
        private int _fadeVersion;
        private string _currentPath;
        private float _slideshowTimer;
        private readonly CancellationTokenSource _cancellation = new();

        public static void TryInitialize()
        {
            if (!SettingsManager.Settings.SendVideoBackgroundToSecondDisplay.Value)
            {
                if (Instance != null)
                {
                    Instance.gameObject.SetActive(false);
                }

                return;
            }

            if (Instance != null)
            {
                Instance.gameObject.SetActive(true);
                Instance.ShowWaiting();
                return;
            }

            if (!TryActivateDisplay())
            {
                return;
            }

            var go = new GameObject(nameof(SecondDisplayManager));
            DontDestroyOnLoad(go);
            go.AddComponent<SecondDisplayManager>();
        }

        public static bool TryActivateDisplay()
        {
            if (Display.displays.Length <= DisplayIndex)
            {
                return false;
            }

            var display = Display.displays[DisplayIndex];
            if (!display.active)
            {
                display.Activate();
            }

            return display.active;
        }

        protected override void SingletonAwake()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
            BuildUi();
            _ready = true;
            ShowWaiting();
            YargLogger.LogInfo("Second display waiting screen started");
        }

        protected override void SingletonDestroy()
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
            DestroyTexture(ref _textureA);
            DestroyTexture(ref _textureB);
            _ready = false;
        }

        private void Update()
        {
            if (!_ready || _showingVideo || _fading || _imagePaths.Count < 2)
            {
                return;
            }

            _slideshowTimer += Time.unscaledDeltaTime;
            if (_slideshowTimer >= SlideshowInterval)
            {
                _slideshowTimer = 0f;
                FadeToNextImage().Forget();
            }
        }

        public void ShowVideo(Texture texture, float aspectRatio)
        {
            if (!_ready || !IsMainThread() || texture == null || aspectRatio <= 0f)
            {
                return;
            }

            _fadeVersion++;
            _fading = false;
            _showingVideo = true;

            _videoImage.texture = texture;
            SetAspect(_videoImage, aspectRatio);

            _videoGroup.alpha = 1f;
            _waitingGroup.alpha = 0f;
        }

        public void ShowWaiting()
        {
            if (!_ready || !IsMainThread())
            {
                return;
            }

            _fadeVersion++;
            _fading = false;
            _showingVideo = false;
            _slideshowTimer = 0f;

            _videoImage.texture = null;
            _videoGroup.alpha = 0f;
            _waitingGroup.alpha = 1f;
            RefreshWaitingText();
            ShowFirstImage().Forget();
        }

        private void BuildUi()
        {
            var cameraObject = new GameObject("Second Display Camera");
            cameraObject.transform.SetParent(transform, false);
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.cullingMask = 0;
            camera.depth = -100;
            camera.orthographic = true;
            camera.allowHDR = false;
            camera.allowMSAA = false;
            camera.targetDisplay = DisplayIndex;

            var cameraData = camera.GetUniversalAdditionalCameraData();
            cameraData.renderType = CameraRenderType.Base;
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;

            var canvasObject = new GameObject("Second Display Canvas");
            canvasObject.transform.SetParent(transform, false);
            var canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.targetDisplay = DisplayIndex;
            canvas.sortingOrder = 0;

            var scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.Expand;

            var letterbox = CreateFullScreenChild(canvasObject.transform, "Letterbox");
            var letterboxImage = letterbox.AddComponent<Image>();
            letterboxImage.color = Color.black;
            letterboxImage.raycastTarget = false;

            var waitingRoot = CreateFullScreenChild(canvasObject.transform, "Waiting");
            _waitingGroup = waitingRoot.AddComponent<CanvasGroup>();
            _waitingGroup.blocksRaycasts = false;
            _waitingGroup.interactable = false;

            _imageA = CreateFitImage(waitingRoot.transform, "Image A");
            _imageB = CreateFitImage(waitingRoot.transform, "Image B");
            _imageB.color = new Color(1f, 1f, 1f, 0f);

            var textBar = CreateChild(waitingRoot.transform, "Text Bar");
            var textBarRect = textBar.GetComponent<RectTransform>();
            textBarRect.anchorMin = new Vector2(0f, 0f);
            textBarRect.anchorMax = new Vector2(1f, 0.18f);
            textBarRect.offsetMin = Vector2.zero;
            textBarRect.offsetMax = Vector2.zero;
            var textBarImage = textBar.AddComponent<Image>();
            textBarImage.color = new Color(0f, 0f, 0f, 0.45f);
            textBarImage.raycastTarget = false;

            var textObject = CreateChild(textBar.transform, "Waiting Text");
            var textRect = textObject.GetComponent<RectTransform>();
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = Vector2.zero;
            textRect.offsetMax = Vector2.zero;
            _waitingText = textObject.AddComponent<TextMeshProUGUI>();
            _waitingText.font = TMP_Settings.defaultFontAsset;
            _waitingText.fontSize = 42f;
            _waitingText.fontStyle = FontStyles.SmallCaps;
            _waitingText.alignment = TextAlignmentOptions.Center;
            _waitingText.color = Color.white;
            _waitingText.outlineWidth = 0.2f;
            _waitingText.outlineColor = Color.black;
            _waitingText.raycastTarget = false;
            RefreshWaitingText();

            var videoRoot = CreateFullScreenChild(canvasObject.transform, "Video");
            _videoGroup = videoRoot.AddComponent<CanvasGroup>();
            _videoGroup.alpha = 0f;
            _videoGroup.blocksRaycasts = false;
            _videoGroup.interactable = false;
            _videoImage = CreateFitImage(videoRoot.transform, "Video Image");
        }

        private void RefreshWaitingText()
        {
            if (_waitingText == null)
            {
                return;
            }

            _waitingText.text = LocalizationManager.TryGetLocalizedKey(
                "Gameplay.SecondDisplay.WaitingForVideo", out var localized)
                ? localized
                : "Waiting for video";
        }

        private async UniTaskVoid ShowFirstImage()
        {
            // Treat the initial load as "busy" so Update doesn't start a fade on top of it.
            int version = _fadeVersion;
            _fading = true;

            var texture = await LoadNextTextureAsync(version);
            if (this == null)
            {
                return;
            }

            if (version != _fadeVersion || _showingVideo)
            {
                DestroyTexture(ref texture);
                return;
            }

            _fading = false;

            var incoming = _showingA ? _imageA : _imageB;
            var outgoing = _showingA ? _imageB : _imageA;
            outgoing.color = new Color(1f, 1f, 1f, 0f);

            if (texture == null)
            {
                // Keep whatever was already on screen if the folder is empty or loading failed.
                if (incoming.texture == null)
                {
                    incoming.color = new Color(1f, 1f, 1f, 0f);
                }
                return;
            }

            AssignTexture(incoming, texture);
            incoming.color = Color.white;
        }

        private async UniTaskVoid FadeToNextImage()
        {
            int version = ++_fadeVersion;
            _fading = true;

            var texture = await LoadNextTextureAsync(version);
            if (this == null)
            {
                return;
            }

            if (version != _fadeVersion || _showingVideo)
            {
                DestroyTexture(ref texture);
                return;
            }

            if (texture == null)
            {
                _fading = false;
                return;
            }

            var incoming = _showingA ? _imageB : _imageA;
            var outgoing = _showingA ? _imageA : _imageB;
            AssignTexture(incoming, texture);
            incoming.color = new Color(1f, 1f, 1f, 0f);

            float elapsed = 0f;
            while (elapsed < FadeDuration)
            {
                if (this == null || version != _fadeVersion || _showingVideo)
                {
                    return;
                }

                elapsed += Time.unscaledDeltaTime;
                float alpha = Mathf.Clamp01(elapsed / FadeDuration);
                incoming.color = new Color(1f, 1f, 1f, alpha);
                outgoing.color = new Color(1f, 1f, 1f, 1f - alpha);
                await UniTask.Yield();
            }

            if (this == null || version != _fadeVersion || _showingVideo)
            {
                return;
            }

            incoming.color = Color.white;
            outgoing.color = new Color(1f, 1f, 1f, 0f);
            _showingA = !_showingA;
            _fading = false;
        }

        private void AssignTexture(RawImage image, Texture2D texture)
        {
            if (image == _imageA)
            {
                DestroyTexture(ref _textureA);
                _textureA = texture;
            }
            else
            {
                DestroyTexture(ref _textureB);
                _textureB = texture;
            }

            image.texture = texture;
            if (texture != null && texture.height > 0)
            {
                SetAspect(image, (float) texture.width / texture.height);
            }
        }

        private static bool IsMainThread()
        {
            return _mainThreadId != 0 && Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        /// <summary>
        /// Picks, decodes and downscales the next waiting image on a worker thread, then uploads it
        /// to the GPU on the main thread. Returns null if there is nothing to show or it was superseded.
        /// </summary>
        private async UniTask<Texture2D> LoadNextTextureAsync(int version)
        {
            var folder = WaitingFolder;
            var currentPath = _currentPath;
            GetTargetSize(out int maxWidth, out int maxHeight);
            var token = _cancellation.Token;

            LoadResult result;
            try
            {
                result = await UniTask.RunOnThreadPool(
                    () => LoadNextImage(folder, currentPath, maxWidth, maxHeight),
                    cancellationToken: token);
            }
            catch (System.OperationCanceledException)
            {
                return null;
            }

            if (this == null || token.IsCancellationRequested)
            {
                return null;
            }

            _imagePaths.Clear();
            _imagePaths.AddRange(result.Paths);

            if (result.Pixels == null || version != _fadeVersion || _showingVideo)
            {
                return null;
            }

            _currentPath = result.Path;

            // Only a display-sized buffer reaches the main thread, so this upload is cheap.
            var texture = new Texture2D(result.Width, result.Height, result.Format, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            texture.LoadRawTextureData(result.Pixels);
            texture.Apply(false, true);
            return texture;
        }

        private static void GetTargetSize(out int width, out int height)
        {
            width = 1920;
            height = 1080;

            if (Display.displays.Length > DisplayIndex)
            {
                var display = Display.displays[DisplayIndex];
                if (display.renderingWidth > 0 && display.renderingHeight > 0)
                {
                    width = display.renderingWidth;
                    height = display.renderingHeight;
                }
            }

            width = Mathf.Clamp(width, 640, MaxTextureWidth);
            height = Mathf.Clamp(height, 360, MaxTextureHeight);
        }

        private sealed class LoadResult
        {
            public List<string> Paths;
            public string Path;
            public byte[] Pixels;
            public int Width;
            public int Height;
            public TextureFormat Format;
        }

        // ---- Everything below runs on a worker thread: no Unity API calls allowed. ----

        private static readonly System.Random WorkerRandom = new();

        private static LoadResult LoadNextImage(string folder, string currentPath, int maxWidth, int maxHeight)
        {
            var result = new LoadResult { Paths = new List<string>() };
            try
            {
                foreach (var ext in ImageExtensions)
                {
                    result.Paths.AddRange(Directory.EnumerateFiles(folder, ext, PathHelper.SafeSearchOptions));
                }
            }
            catch (System.Exception ex)
            {
                YargLogger.LogFormatWarning<string, string>("Failed to list waiting images in `{0}`: {1}", folder, ex.Message);
                return result;
            }

            if (result.Paths.Count == 0)
            {
                return result;
            }

            // Try a random image first (different from the current one), then fall back to the
            // rest so a single broken file doesn't blank the slideshow.
            var candidates = new List<string>(result.Paths);
            if (candidates.Count > 1)
            {
                candidates.Remove(currentPath);
            }

            lock (WorkerRandom)
            {
                for (int i = candidates.Count - 1; i > 0; i--)
                {
                    int j = WorkerRandom.Next(i + 1);
                    (candidates[i], candidates[j]) = (candidates[j], candidates[i]);
                }
            }

            foreach (var path in candidates)
            {
                if (TryDecode(path, maxWidth, maxHeight, result))
                {
                    result.Path = path;
                    return result;
                }
            }

            return result;
        }

        private static unsafe bool TryDecode(string path, int maxWidth, int maxHeight, LoadResult result)
        {
            try
            {
                using var image = YARGImage.Load(path);
                if (image == null || !image.IsValid)
                {
                    YargLogger.LogFormatWarning<string>("Failed to decode waiting image `{0}`", path);
                    return false;
                }

                int srcChannels = image.Format switch
                {
                    ImageFormat.Grayscale       => 1,
                    ImageFormat.GrayScale_Alpha => 2,
                    ImageFormat.RGB             => 3,
                    ImageFormat.RGBA            => 4,
                    _                           => 0,
                };
                if (srcChannels == 0)
                {
                    return false;
                }

                bool hasAlpha = srcChannels == 2 || srcChannels == 4;
                int dstChannels = hasAlpha ? 4 : 3;

                int srcWidth = image.Width;
                int srcHeight = image.Height;
                double scale = System.Math.Min(1.0,
                    System.Math.Min((double) maxWidth / srcWidth, (double) maxHeight / srcHeight));
                int dstWidth = System.Math.Max(1, (int) System.Math.Round(srcWidth * scale));
                int dstHeight = System.Math.Max(1, (int) System.Math.Round(srcHeight * scale));

                var pixels = new byte[dstWidth * dstHeight * dstChannels];
                byte* src = image.Data;
                long srcStride = (long) srcWidth * srcChannels;
                long* sum = stackalloc long[4];

                // Box filter: each destination pixel is the average of the source block it covers.
                for (int dy = 0; dy < dstHeight; dy++)
                {
                    int sy0 = (int) ((long) dy * srcHeight / dstHeight);
                    int sy1 = System.Math.Max(sy0 + 1, (int) ((long) (dy + 1) * srcHeight / dstHeight));

                    // The decoder returns rows top-down; Unity textures are bottom-up.
                    int outRow = (dstHeight - 1 - dy) * dstWidth * dstChannels;

                    for (int dx = 0; dx < dstWidth; dx++)
                    {
                        int sx0 = (int) ((long) dx * srcWidth / dstWidth);
                        int sx1 = System.Math.Max(sx0 + 1, (int) ((long) (dx + 1) * srcWidth / dstWidth));

                        sum[0] = sum[1] = sum[2] = sum[3] = 0;
                        for (int sy = sy0; sy < sy1; sy++)
                        {
                            byte* row = src + sy * srcStride;
                            for (int sx = sx0; sx < sx1; sx++)
                            {
                                byte* px = row + sx * srcChannels;
                                for (int c = 0; c < srcChannels; c++)
                                {
                                    sum[c] += px[c];
                                }
                            }
                        }

                        long count = (long) (sy1 - sy0) * (sx1 - sx0);
                        int o = outRow + dx * dstChannels;
                        if (srcChannels <= 2)
                        {
                            var gray = (byte) (sum[0] / count);
                            pixels[o] = gray;
                            pixels[o + 1] = gray;
                            pixels[o + 2] = gray;
                            if (hasAlpha)
                            {
                                pixels[o + 3] = (byte) (sum[1] / count);
                            }
                        }
                        else
                        {
                            for (int c = 0; c < dstChannels; c++)
                            {
                                pixels[o + c] = (byte) (sum[c] / count);
                            }
                        }
                    }
                }

                result.Pixels = pixels;
                result.Width = dstWidth;
                result.Height = dstHeight;
                result.Format = hasAlpha ? TextureFormat.RGBA32 : TextureFormat.RGB24;
                return true;
            }
            catch (System.Exception ex)
            {
                YargLogger.LogFormatWarning<string, string>("Failed to load waiting image `{0}`: {1}", path, ex.Message);
                return false;
            }
        }

        private static void SetAspect(RawImage image, float aspectRatio)
        {
            var fitter = image.GetComponent<AspectRatioFitter>();
            if (fitter != null)
            {
                fitter.aspectRatio = aspectRatio;
            }
        }

        private static void DestroyTexture(ref Texture2D texture)
        {
            if (texture == null)
            {
                return;
            }

            Destroy(texture);
            texture = null;
        }

        private static RawImage CreateFitImage(Transform parent, string name)
        {
            var child = CreateFullScreenChild(parent, name);
            var image = child.AddComponent<RawImage>();
            image.color = Color.white;
            image.raycastTarget = false;

            var fitter = child.AddComponent<AspectRatioFitter>();
            fitter.aspectMode = AspectRatioFitter.AspectMode.FitInParent;
            fitter.aspectRatio = 16f / 9f;
            return image;
        }

        private static GameObject CreateFullScreenChild(Transform parent, string name)
        {
            var child = CreateChild(parent, name);
            var rect = child.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            return child;
        }

        private static GameObject CreateChild(Transform parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            var rect = child.AddComponent<RectTransform>();
            rect.pivot = new Vector2(0.5f, 0.5f);
            return child;
        }
    }
}
