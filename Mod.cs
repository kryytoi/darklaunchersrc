using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DarkVisualsLauncher1
{
    /// <summary>
    /// Модель одного мода в разделе "Мастерская".
    /// Данные приходят из Modrinth (открытый каталог модов, без API-ключа).
    /// </summary>
    public class ModItem : INotifyPropertyChanged
    {
        public string ProjectId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string IconUrl { get; set; } = string.Empty;
        public long Downloads { get; set; }

        public string DownloadsText => $"⬇ {Downloads:N0} скачиваний";

        private string _statusText = "Скачать";
        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText == value) return;
                _statusText = value;
                OnPropertyChanged();
            }
        }

        private bool _isEnabled = true;
        public bool IsEnabled
        {
            get => _isEnabled;
            set
            {
                if (_isEnabled == value) return;
                _isEnabled = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}