using System;
using System.Threading.Tasks;
using VK_UI3;
using VK_UI3.Addons;

namespace VkMiniPlayer
{
    /// <summary>
    /// Аддон мини-плеера, который открывает отдельное окно
    /// с элементами управления воспроизведением, кнопками закрыть и закрепить.
    /// </summary>
    public class VkMiniPlayer : IAddon
    {
        // ===== МЕТАДАННЫЕ =====
        public string Id => "vk_mini_player";
        public string Name => "VK M Miniplayer";
        public string Version => "1.0.0";
        public string Author => "Werhes";
        public string Description => "Мини-плеер в отдельном окне с кнопкой закрепить поверх всех окон";

        // ===== ПОЛЯ =====
        private VkMiniPlayerWindow _miniPlayerWindow;
        private bool _isInitialized;

        public Task InitializeAsync()
        {
            try
            {
                // Открываем окно мини-плеера в UI-потоке
                MainWindow.dispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        _miniPlayerWindow = new VkMiniPlayerWindow();
                        _miniPlayerWindow.Activate();
                        _isInitialized = true;

                        System.Diagnostics.Debug.WriteLine("[VkMiniPlayer] Мини-плеер запущен!");
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[VkMiniPlayer] Ошибка при создании окна: {ex.Message}");
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMiniPlayer] Ошибка инициализации: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        public Task ShutdownAsync()
        {
            try
            {
                if (_miniPlayerWindow != null)
                {
                    MainWindow.dispatcherQueue.TryEnqueue(() =>
                    {
                        try
                        {
                            _miniPlayerWindow.Close();
                        }
                        catch { }
                    });
                    _miniPlayerWindow = null;
                }

                _isInitialized = false;
                System.Diagnostics.Debug.WriteLine("[VkMiniPlayer] Мини-плеер выгружен!");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VkMiniPlayer] Ошибка при выгрузке: {ex.Message}");
            }

            return Task.CompletedTask;
        }
    }
}