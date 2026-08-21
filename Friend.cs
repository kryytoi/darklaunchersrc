using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DarkVisualsLauncher1
{
    /// <summary>
    /// Модель одного "друга" в разделе "Друны".
    /// Реализует INotifyPropertyChanged, чтобы изменения ника сразу
    /// отражались в UI (например, при редактировании через карандашик).
    /// </summary>
    public class Friend : INotifyPropertyChanged
    {
        private string _nickname = string.Empty;

        public string Nickname
        {
            get => _nickname;
            set
            {
                if (_nickname == value) return;
                _nickname = value;
                OnPropertyChanged();
            }
        }

        // Первая буква ника — используем как маленькую "аватарку"-заглушку.
        public string InitialLetter => string.IsNullOrEmpty(Nickname) ? "?" : Nickname.Substring(0, 1).ToUpper();

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
}
