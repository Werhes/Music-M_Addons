using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using VK_UI3.Helpers;
using VK_UI3.VKs.IVK;
using Windows.Media;
using Windows.Media.Playback;
using WinRT.Interop;

namespace VkMiniPlayer
{
    /// <summary>
    /// Окно мини-плеера, которое открывается в отдельном окне.
    /// Содержит кнопки: закрыть, закрепить (поверх всех окон),
    /// а также элементы управления воспроизведением.
    /// </summary>
    public sealed partial class VkMiniPlayerWindow : Window
    {
        #region Fields

        private DateTime _lastPositionUpdate = DateTime.MinValue;
        private bool _isPinned = false;
        private AppWindow _appWindow;
        private OverlappedPresenter _presenter;

        #endregion

        #region Win32 Imports

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr LoadImage(IntPtr hinst, string lpszName, uint uType, int cxDesired, int cyDesired, uint fuLoad);

        private const uint IMAGE_ICON = 1;
        private const uint LR_LOADFROMFILE = 0x00000010;
        private const uint WM_SETICON = 0x0080;

        #endregion

        #region Properties

        public MediaPlayer MediaPlayer
        {
            get => VK_UI3.Services.MediaPlayerService.MediaPlayer;
            set => VK_UI3.Services.MediaPlayerService.MediaPlayer = value;
        }

        public async Task<ExtendedAudio> GetTrackDataAsync()
        {
            try
            {
                return await _TrackDataThisGet();
            }
            catch
            {
                return null;
            }
        }

        public async Task<string> GetThumbnailAsync()
        {
            try
            {
                var trackData = await GetTrackDataAsync();
                if (trackData?.audio?.Album?.Thumb == null) return null;

                return trackData.audio.Album.Thumb.Photo600
                     ?? trackData.audio.Album.Thumb.Photo300
                     ?? trackData.audio.Album.Thumb.Photo270
                     ?? trackData.audio.Album.Thumb.Photo68
                     ?? trackData.audio.Album.Thumb.Photo34
                     ?? null;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Constructor

        public VkMiniPlayerWindow()
        {
            this.InitializeComponent();

            // Настраиваем окно
            var hwnd = WindowNative.GetWindowHandle(this);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);
            _presenter = _appWindow.Presenter as OverlappedPresenter;

            if (_presenter != null)
            {
                _presenter.IsResizable = false;
                _presenter.IsMaximizable = false;
                _presenter.IsMinimizable = false;
            }

            // Устанавливаем размер окна
            _appWindow.Resize(new Windows.Graphics.SizeInt32(320, 480));

            // Загружаем иконку из основного приложения VK M
            LoadAppIcon();

            // Подписываемся на события плеера
            VK_UI3.Services.MediaPlayerService.AudioPlayedChangeEvent += OnTrackChanged;
            VK_UI3.Services.MediaPlayerService.PositionChanged += OnPositionChanged;
            VK_UI3.Services.MediaPlayerService.MediaPlayer.CurrentStateChanged += OnPlaybackStateChanged;

            // Загружаем данные
            _ = SetDataAsync();
            UpdatePlayPauseIcon();
        }

        #endregion

        #region Icon Loading

        /// <summary>
        /// Загружает иконку из основного приложения VK M (icon.ico)
        /// и устанавливает её для окна.
        /// </summary>
        private void LoadAppIcon()
        {
            try
            {
                // Иконка приложения VK M находится в папке с приложением
                string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico");
                if (File.Exists(iconPath))
                {
                    var hwnd = WindowNative.GetWindowHandle(this);
                    IntPtr iconHandle = LoadImage(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
                    if (iconHandle != IntPtr.Zero)
                    {
                        SendMessage(hwnd, WM_SETICON, IntPtr.Zero, iconHandle);
                    }
                }

                // Также загружаем иконку для отображения в заголовке окна
                string assetsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "icon.ico");
                if (File.Exists(assetsPath))
                {
                    var bitmap = new BitmapImage(new Uri(assetsPath));
                    AppIcon.Source = bitmap;
                }
                else if (File.Exists(iconPath))
                {
                    // Пробуем загрузить StoreLogo если есть
                    string pngPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "StoreLogo.scale-400.png");
                    if (File.Exists(pngPath))
                    {
                        var bitmap = new BitmapImage(new Uri(pngPath));
                        AppIcon.Source = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMiniPlayer] Error loading icon: {ex.Message}");
            }
        }

        #endregion

        #region Event Handlers

        private void OnTrackChanged(object sender, EventArgs e)
        {
            this.DispatcherQueue.TryEnqueue(async () =>
            {
                await SetDataAsync();
            });
        }

        private void OnPositionChanged(object sender, TimeSpan e)
        {
            try
            {
                var now = DateTime.Now;
                if ((now - _lastPositionUpdate).TotalMilliseconds < 250)
                    return;
                _lastPositionUpdate = now;

                this.DispatcherQueue.TryEnqueue(() =>
                {
                    PositionSlider.Value = e.TotalSeconds;
                });
            }
            catch { }
        }

        private void OnPlaybackStateChanged(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            this.DispatcherQueue.TryEnqueue(() => UpdatePlayPauseIcon());
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            CloseWindow();
        }

        private void PinButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePin();
        }

        private void PositionSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
        {
            if (MediaPlayer == null) return;
            MediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
        }

        private void PrevButton_Click(object sender, RoutedEventArgs e)
        {
            VK_UI3.Services.MediaPlayerService.HandlePreviousTrack();
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (MediaPlayer == null) return;

            switch (MediaPlayer.CurrentState)
            {
                case MediaPlayerState.Playing:
                    MediaPlayer.Pause();
                    break;
                case MediaPlayerState.Paused:
                    MediaPlayer.Play();
                    break;
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            VK_UI3.Services.MediaPlayerService.PlayNextTrack();
        }

        #endregion

        #region Pin / Close Logic

        /// <summary>
        /// Переключает режим закрепления окна поверх всех окон.
        /// </summary>
        private void TogglePin()
        {
            _isPinned = !_isPinned;

            if (_presenter != null)
            {
                _presenter.IsAlwaysOnTop = _isPinned;
            }

            // Меняем иконку кнопки: закрепка (залитая) / открепка
            if (_isPinned)
            {
                PinIcon.Glyph = "\uE77A"; // Залитая булавка (закреплено)
            }
            else
            {
                PinIcon.Glyph = "\uE718"; // Пустая булавка (откреплено)
            }

            System.Diagnostics.Debug.WriteLine($"[VkMiniPlayer] Pin toggled: {_isPinned}");
        }

        /// <summary>
        /// Закрывает окно мини-плеера и отписывается от событий.
        /// </summary>
        private void CloseWindow()
        {
            // Отписываемся от событий
            VK_UI3.Services.MediaPlayerService.AudioPlayedChangeEvent -= OnTrackChanged;
            VK_UI3.Services.MediaPlayerService.PositionChanged -= OnPositionChanged;
            VK_UI3.Services.MediaPlayerService.MediaPlayer.CurrentStateChanged -= OnPlaybackStateChanged;

            this.Close();
        }

        #endregion

        #region UI Updates

        private void UpdatePlayPauseIcon()
        {
            if (MediaPlayer == null) return;

            if (MediaPlayer.CurrentState == MediaPlayerState.Playing)
            {
                PlayPauseIcon.Glyph = "\uE769"; // Pause icon
            }
            else
            {
                PlayPauseIcon.Glyph = "\uE768"; // Play icon
            }
        }

        private async Task SetDataAsync()
        {
            try
            {
                var track = await GetTrackDataAsync();
                if (track?.audio == null)
                {
                    // Если трека нет, показываем заглушку
                    TrackName.Text = "Не играет";
                    ArtistName.Text = "";
                    CoverImage.Source = null;
                    CoverNote.Visibility = Visibility.Visible;
                    PositionSlider.Maximum = 1;
                    PositionSlider.Value = 0;
                    return;
                }

                PositionSlider.Maximum = track.audio.Duration;

                // Плавная смена обложки
                var thumb = await GetThumbnailAsync();
                AnimateCoverImage(thumb);

                TrackName.Text = track.audio.Title ?? "Unknown";
                ArtistName.Text = track.audio.Artist ?? "Unknown";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMiniPlayer] Error in SetDataAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Плавно меняет обложку с fade-анимацией.
        /// </summary>
        private void AnimateCoverImage(string imageUrl)
        {
            try
            {
                if (string.IsNullOrEmpty(imageUrl))
                {
                    CoverImage.Source = null;
                    CoverNote.Visibility = Visibility.Visible;
                    return;
                }

                var storyboard = new Storyboard();

                // Fade-out: 0.25 сек
                var fadeOut = new DoubleAnimation
                {
                    From = 1.0,
                    To = 0.0,
                    Duration = TimeSpan.FromMilliseconds(250),
                    EnableDependentAnimation = true
                };
                Storyboard.SetTarget(fadeOut, CoverImage);
                Storyboard.SetTargetProperty(fadeOut, "Opacity");

                EventHandler<object> onFadeOutCompleted = null;
                onFadeOutCompleted = (s, e) =>
                {
                    try
                    {
                        storyboard.Completed -= onFadeOutCompleted;
                        storyboard.Children.Clear();

                        var uri = new Uri(imageUrl, UriKind.Absolute);
                        var bitmap = new BitmapImage(uri);
                        CoverImage.Source = bitmap;
                        CoverNote.Visibility = Visibility.Collapsed;

                        // Fade-in: 0.5 сек
                        var fadeIn = new DoubleAnimation
                        {
                            From = 0.0,
                            To = 1.0,
                            Duration = TimeSpan.FromMilliseconds(500),
                            EnableDependentAnimation = true
                        };
                        Storyboard.SetTarget(fadeIn, CoverImage);
                        Storyboard.SetTargetProperty(fadeIn, "Opacity");
                        storyboard.Children.Add(fadeIn);
                        storyboard.Begin();
                    }
                    catch
                    {
                        CoverImage.Source = null;
                        CoverNote.Visibility = Visibility.Visible;
                    }
                };

                storyboard.Completed += onFadeOutCompleted;
                storyboard.Children.Add(fadeOut);
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMiniPlayer] Error in AnimateCoverImage: {ex.Message}");
                CoverImage.Source = null;
                CoverNote.Visibility = Visibility.Visible;
            }
        }

        #endregion

        #region Helper Methods

        public static async Task<ExtendedAudio> _TrackDataThisGet(bool forced = false)
        {
            var iVK = VK_UI3.Services.MediaPlayerService.iVKGetAudio;
            if (iVK != null && iVK.countTracks != 0)
            {
                return await iVK.GetTrackPlay(forced);
            }
            return VK_UI3.Services.MediaPlayerService._trackDataThis;
        }

        #endregion
    }
}