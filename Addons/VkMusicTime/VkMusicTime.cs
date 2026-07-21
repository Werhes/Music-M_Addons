using System;
using System.Threading.Tasks;
using VK_UI3;
using VK_UI3.Addons;
using VK_UI3.DB;
using VK_UI3.Services;
using VK_UI3.Views.Notification;

namespace VkMusicTime
{
    /// <summary>
    /// Аддон для отслеживания времени, проведённого в VK M.
    /// Считает общее время, время за сегодня, время прослушивания музыки.
    /// Показывает уведомления о достижениях каждый час.
    /// </summary>
    public class VkMusicTime : IAddon
    {
        // ===== МЕТАДАННЫЕ =====
        public string Id => "vk_music_time";
        public string Name => "VkMusicTime";
        public string Version => "1.0.0";
        public string Author => "Werhes";
        public string Description => "Отслеживает время в VK M и уведомляет о достижениях";

        // ===== ПОЛЯ ДЛЯ ХРАНЕНИЯ СОСТОЯНИЯ =====
        private bool _isRunning;
        private DateTime _sessionStart;
        private DateTime _appStart;
        private System.Threading.Timer _saveTimer;
        private System.Threading.Timer _achievementTimer;

        // Флаг для отслеживания, играет ли музыка
        private bool _isMusicPlaying;
        private TimeSpan _lastPosition;
        private DateTime _musicSessionStart;

        // ===== КЛЮЧИ ДЛЯ ХРАНЕНИЯ В БАЗЕ =====
        private const string KEY_TOTAL_SECONDS = "vkmusictime_total_seconds";
        private const string KEY_LISTENING_SECONDS = "vkmusictime_listening_seconds";
        private const string KEY_TODAY_DATE = "vkmusictime_today_date";
        private const string KEY_TODAY_SECONDS = "vkmusictime_today_seconds";
        private const string KEY_LAST_ACHIEVEMENT = "vkmusictime_last_achievement";
        private const string KEY_FIRST_LAUNCH = "vkmusictime_first_launch";

        // ===== ПОРОГИ ДОСТИЖЕНИЙ (В ЧАСАХ) =====
        // Каждый час до 24, затем особые пороги
        private static readonly int[] HourlyAchievements = GenerateHourlyAchievements();
        private static readonly int[] SpecialAchievements = { 24, 50, 100, 200, 500, 1000 };

        private static int[] GenerateHourlyAchievements()
        {
            var hours = new int[23]; // 1..23
            for (int i = 0; i < 23; i++)
                hours[i] = i + 1;
            return hours;
        }

        public Task InitializeAsync()
        {
            _appStart = DateTime.Now;
            _sessionStart = DateTime.Now;
            _isMusicPlaying = false;
            _lastPosition = TimeSpan.Zero;

            // Сбрасываем счётчик сегодняшнего дня, если новый день
            CheckAndResetDailyCounter();

            // Запоминаем первый запуск
            if (SettingsTable.GetSetting(KEY_FIRST_LAUNCH) == null)
            {
                SettingsTable.SetSetting(KEY_FIRST_LAUNCH, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            }

            // Подписываемся на события приложения
            App.Current.Exit += OnAppExit;

            // Подписываемся на события плеера для отслеживания прослушивания
            MediaPlayerService.AudioPlayedChangeEvent += OnAudioPlayedChange;
            MediaPlayerService.PositionChanged += OnPositionChanged;

            // Запускаем таймер автосохранения каждые 30 секунд
            _saveTimer = new System.Threading.Timer(
                _ => SaveCurrentSession(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30)
            );

            // Запускаем таймер проверки достижений каждые 30 секунд
            _achievementTimer = new System.Threading.Timer(
                _ => CheckAchievements(),
                null,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(30)
            );

            _isRunning = true;

            // Показываем приветственное уведомление при первом запуске
            ShowWelcomeNotification();

            System.Diagnostics.Debug.WriteLine("[VkMusicTime] Аддон инициализирован!");
            return Task.CompletedTask;
        }

        public Task ShutdownAsync()
        {
            _isRunning = false;

            // Отписываемся от событий
            App.Current.Exit -= OnAppExit;
            MediaPlayerService.AudioPlayedChangeEvent -= OnAudioPlayedChange;
            MediaPlayerService.PositionChanged -= OnPositionChanged;

            // Останавливаем таймеры
            _saveTimer?.Dispose();
            _achievementTimer?.Dispose();

            // Сохраняем текущую сессию
            SaveCurrentSession();

            System.Diagnostics.Debug.WriteLine("[VkMusicTime] Аддон выгружен!");
            return Task.CompletedTask;
        }

        // ===== ОБРАБОТЧИКИ СОБЫТИЙ =====

        /// <summary>
        /// Вызывается при закрытии приложения
        /// </summary>
        private void OnAppExit(object sender, object e)
        {
            SaveCurrentSession();
        }

        /// <summary>
        /// Вызывается при смене состояния воспроизведения (играет/пауза)
        /// </summary>
        private void OnAudioPlayedChange(object sender, EventArgs e)
        {
            try
            {
                var player = MediaPlayerService.MediaPlayer;
                if (player != null && player.PlaybackSession != null)
                {
                    var state = player.PlaybackSession.PlaybackState;
                    bool isPlaying = (state == Windows.Media.Playback.MediaPlaybackState.Playing);

                    if (isPlaying && !_isMusicPlaying)
                    {
                        // Музыка начала играть
                        _isMusicPlaying = true;
                        _musicSessionStart = DateTime.Now;
                        System.Diagnostics.Debug.WriteLine("[VkMusicTime] Музыка начала играть");
                    }
                    else if (!isPlaying && _isMusicPlaying)
                    {
                        // Музыка остановилась — сохраняем время прослушивания
                        _isMusicPlaying = false;
                        SaveListeningTime();
                        System.Diagnostics.Debug.WriteLine("[VkMusicTime] Музыка остановилась");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMusicTime] Ошибка в OnAudioPlayedChange: {ex.Message}");
            }
        }

        /// <summary>
        /// Вызывается при изменении позиции трека
        /// </summary>
        private void OnPositionChanged(object sender, TimeSpan position)
        {
            // Если позиция изменилась и музыка играет — отмечаем это
            if (_isMusicPlaying && position > _lastPosition)
            {
                _lastPosition = position;
            }
            else if (position < _lastPosition)
            {
                // Трек переключился — сбрасываем позицию
                _lastPosition = position;
            }
        }

        // ===== ОСНОВНАЯ ЛОГИКА =====

        /// <summary>
        /// Сохранить время прослушивания музыки
        /// </summary>
        private void SaveListeningTime()
        {
            try
            {
                var duration = (DateTime.Now - _musicSessionStart).TotalSeconds;
                if (duration < 1) return;

                var savedListening = GetTotalSeconds(KEY_LISTENING_SECONDS);
                var newListening = savedListening + (long)duration;
                SettingsTable.SetSetting(KEY_LISTENING_SECONDS, newListening.ToString());

                System.Diagnostics.Debug.WriteLine($"[VkMusicTime] Сохранено время прослушивания: {duration:F0} сек");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMusicTime] Ошибка сохранения времени прослушивания: {ex.Message}");
            }
        }

        /// <summary>
        /// Сохранить текущую сессию в базу данных
        /// </summary>
        private void SaveCurrentSession()
        {
            if (!_isRunning) return;

            try
            {
                var now = DateTime.Now;
                var sessionDuration = (now - _sessionStart).TotalSeconds;

                if (sessionDuration < 1) return;

                // Обновляем общее время
                var savedTotal = GetTotalSeconds(KEY_TOTAL_SECONDS);
                var newTotal = savedTotal + (long)sessionDuration;
                SettingsTable.SetSetting(KEY_TOTAL_SECONDS, newTotal.ToString());

                // Обновляем время за сегодня
                var savedToday = GetTotalSeconds(KEY_TODAY_SECONDS);
                var newToday = savedToday + (long)sessionDuration;
                SettingsTable.SetSetting(KEY_TODAY_SECONDS, newToday.ToString());

                // Если музыка всё ещё играет, добавляем время прослушивания
                if (_isMusicPlaying)
                {
                    var listenDuration = (now - _musicSessionStart).TotalSeconds;
                    if (listenDuration >= 1)
                    {
                        var savedListening = GetTotalSeconds(KEY_LISTENING_SECONDS);
                        var newListening = savedListening + (long)listenDuration;
                        SettingsTable.SetSetting(KEY_LISTENING_SECONDS, newListening.ToString());
                        _musicSessionStart = now;
                    }
                }

                // Сбрасываем время сессии
                _sessionStart = now;

                System.Diagnostics.Debug.WriteLine($"[VkMusicTime] Сохранено. Всего: {FormatTime(newTotal)}, Сегодня: {FormatTime(newToday)}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMusicTime] Ошибка сохранения: {ex.Message}");
            }
        }

        /// <summary>
        /// Проверить и сбросить ежедневный счётчик, если наступил новый день
        /// </summary>
        private void CheckAndResetDailyCounter()
        {
            var savedDate = SettingsTable.GetSetting(KEY_TODAY_DATE);
            var today = DateTime.Now.ToString("yyyy-MM-dd");

            if (savedDate == null || savedDate.settingValue != today)
            {
                SettingsTable.SetSetting(KEY_TODAY_DATE, today);
                SettingsTable.SetSetting(KEY_TODAY_SECONDS, "0");
                System.Diagnostics.Debug.WriteLine("[VkMusicTime] Сброшен ежедневный счётчик");
            }
        }

        /// <summary>
        /// Проверить достижения и показать уведомление
        /// </summary>
        private void CheckAchievements()
        {
            try
            {
                var totalSeconds = GetTotalSeconds(KEY_TOTAL_SECONDS);
                var totalHours = totalSeconds / 3600.0;
                var lastAchievement = SettingsTable.GetSetting(KEY_LAST_ACHIEVEMENT);
                var lastAchievementHours = lastAchievement != null ? int.Parse(lastAchievement.settingValue) : 0;

                // Проверяем почасовые достижения (1, 2, 3... 23)
                foreach (var hours in HourlyAchievements)
                {
                    if (totalHours >= hours && hours > lastAchievementHours)
                    {
                        UnlockAchievement(hours, totalSeconds);
                        return;
                    }
                }

                // Проверяем специальные достижения (24, 50, 100, 200, 500, 1000)
                foreach (var hours in SpecialAchievements)
                {
                    if (totalHours >= hours && hours > lastAchievementHours)
                    {
                        UnlockAchievement(hours, totalSeconds);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMusicTime] Ошибка проверки достижений: {ex.Message}");
            }
        }

        /// <summary>
        /// Разблокировать достижение и показать уведомление
        /// </summary>
        private void UnlockAchievement(int hours, long totalSeconds)
        {
            SettingsTable.SetSetting(KEY_LAST_ACHIEVEMENT, hours.ToString());

            var todaySeconds = GetTotalSeconds(KEY_TODAY_SECONDS);
            var listeningSeconds = GetTotalSeconds(KEY_LISTENING_SECONDS);
            var firstLaunch = SettingsTable.GetSetting(KEY_FIRST_LAUNCH)?.settingValue ?? "неизвестно";

            ShowAchievementNotification(hours, totalSeconds, todaySeconds, listeningSeconds, firstLaunch);

            System.Diagnostics.Debug.WriteLine($"[VkMusicTime] Достижение: {hours} часов в VK M!");
        }

        // ===== УВЕДОМЛЕНИЯ =====

        /// <summary>
        /// Показать приветственное уведомление
        /// </summary>
        private void ShowWelcomeNotification()
        {
            try
            {
                var firstLaunch = SettingsTable.GetSetting(KEY_FIRST_LAUNCH)?.settingValue;
                if (firstLaunch != null)
                {
                    // Уже был запуск, не показываем
                    return;
                }

                MainWindow.dispatcherQueue.TryEnqueue(() =>
                {
                    new Notification(
                        "⏱ VkMusicTime активирован!",
                        "Теперь я буду отслеживать время, проведённое в VK M.\n" +
                        "Каждый час ты будешь получать уведомление со статистикой!"
                    );
                });
            }
            catch { }
        }

        /// <summary>
        /// Показать уведомление о достижении
        /// </summary>
        private void ShowAchievementNotification(int hours, long totalSeconds, long todaySeconds, long listeningSeconds, string firstLaunch)
        {
            var (emoji, title) = GetAchievementInfo(hours);
            var totalTime = FormatTime(totalSeconds);
            var todayTime = FormatTime(todaySeconds);
            var listeningTime = FormatTime(listeningSeconds);

            MainWindow.dispatcherQueue.TryEnqueue(() =>
            {
                new Notification(
                    $"{emoji} {title}",
                    $"⏱ Всего: {totalTime}\n" +
                    $"📅 Сегодня: {todayTime}\n" +
                    $"🎵 Прослушано: {listeningTime}\n" +
                    $"📆 В приложении с: {firstLaunch}"
                );
            });
        }

        /// <summary>
        /// Получить эмодзи и название для достижения
        /// </summary>
        private static (string emoji, string title) GetAchievementInfo(int hours)
        {
            return hours switch
            {
                1 => ("🌱", "Первый час в VK M!"),
                2 => ("🎵", "2 часа! Входишь во вкус!"),
                3 => ("🎶", "3 часа! Музыка — твоя стихия!"),
                4 => ("⏰", "4 часа! Отличный старт!"),
                5 => ("🌿", "5 часов! Начало положено!"),
                6 => ("🎧", "6 часов! Полдня в ритме!"),
                7 => ("📀", "7 часов! Неделя привычек!"),
                8 => ("💼", "8 часов! Рабочий день с музыкой!"),
                9 => ("🏃", "9 часов! Почти рекорд!"),
                10 => ("🌳", "10 часов! Уже привыкаешь?"),
                11 => ("⚡", "11 часов! На полпути к суткам!"),
                12 => ("🎯", "12 часов! Половина суток!"),
                13 => ("🔥", "13 часов! Час за часом!"),
                14 => ("💪", "14 часов! Ты неутомим!"),
                15 => ("⭐", "15 часов! Звёздный слушатель!"),
                16 => ("🏅", "16 часов! Достоин медали!"),
                17 => ("🚀", "17 часов! Космический полёт!"),
                18 => ("💎", "18 часов! Чистое наслаждение!"),
                19 => ("🌟", "19 часов! Сияющий результат!"),
                20 => ("👏", "20 часов! Феноменальная выдержка!"),
                21 => ("🎪", "21 час! Почти цирковой трюк!"),
                22 => ("🏆", "22 часа! Ты — чемпион!"),
                23 => ("🎂", "23 часа! Ещё час до суток!"),
                24 => ("⭐", "Целые сутки в VK M! 🎉"),
                50 => ("🌟", "50 часов! Настоящий меломан!"),
                100 => ("💫", "100 часов! Ты живёшь музыкой!"),
                200 => ("👑", "200 часов! Легендарный слушатель!"),
                500 => ("💎", "500 часов! VK M — твой второй дом!"),
                1000 => ("🔥", "1000 часов! Ты — икона стиля!"),
                _ => ("🎯", $"{hours} часов в VK M!")
            };
        }

        // ===== ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ =====

        /// <summary>
        /// Получить общее количество секунд из БД
        /// </summary>
        private long GetTotalSeconds(string key)
        {
            var setting = SettingsTable.GetSetting(key);
            if (setting == null || string.IsNullOrEmpty(setting.settingValue))
                return 0;

            if (long.TryParse(setting.settingValue, out long seconds))
                return seconds;

            return 0;
        }

        /// <summary>
        /// Форматировать время в человекочитаемый вид
        /// </summary>
        private static string FormatTime(long totalSeconds)
        {
            var ts = TimeSpan.FromSeconds(totalSeconds);

            if (ts.TotalDays >= 1)
                return $"{(int)ts.TotalDays} д {ts.Hours} ч {ts.Minutes} мин";
            else if (ts.TotalHours >= 1)
                return $"{(int)ts.TotalHours} ч {ts.Minutes} мин";
            else
                return $"{ts.Minutes} мин {ts.Seconds} сек";
        }

        /// <summary>
        /// Получить статистику (может быть вызвано из другого аддона или через рефлексию)
        /// </summary>
        public static string GetStatistics()
        {
            var totalSeconds = long.TryParse(
                SettingsTable.GetSetting(KEY_TOTAL_SECONDS)?.settingValue, out var total)
                ? total : 0;

            var todaySeconds = long.TryParse(
                SettingsTable.GetSetting(KEY_TODAY_SECONDS)?.settingValue, out var today)
                ? today : 0;

            var listeningSeconds = long.TryParse(
                SettingsTable.GetSetting(KEY_LISTENING_SECONDS)?.settingValue, out var listening)
                ? listening : 0;

            var firstLaunch = SettingsTable.GetSetting(KEY_FIRST_LAUNCH)?.settingValue ?? "неизвестно";

            return $"⏱ Всего: {FormatTime(totalSeconds)}\n" +
                   $"📅 Сегодня: {FormatTime(todaySeconds)}\n" +
                   $"🎵 Прослушано: {FormatTime(listeningSeconds)}\n" +
                   $"📆 В приложении с: {firstLaunch}";
        }
    }
}