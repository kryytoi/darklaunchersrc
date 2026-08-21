using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using DarkVisualsLauncher1.Security;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace DarkVisualsLauncher1
{
    public partial class MainWindow : Window
    {
        // Один HttpClient на всё приложение
        private static readonly HttpClient Http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                SslProtocols = System.Security.Authentication.SslProtocols.Tls12 |
                               System.Security.Authentication.SslProtocols.Tls13,
                AutomaticDecompression = System.Net.DecompressionMethods.All
            };

            var client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromSeconds(60);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("DarkVisualsLauncher/1.0");
            return client;
        }

        private const string ServerBaseUrl = "https://darkvisuals.vercel.app";

        private readonly string githubVersionUrl = "https://raw.githubusercontent.com/kryytoi/WDdwdw/refs/heads/main/Version.txt";

        private const string CurrentLoaderVersion = "2";

        private string _currentRole = "User";
        private string _currentLogin = string.Empty;
        private string _currentHwid = string.Empty;

        private readonly string _dataFolder;
        private readonly string _friendsFilePath;

        // ===== ПРОФИЛЬ =====
        private string _avatarFilePath = string.Empty;
        private string _profileSettingsPath = string.Empty;
        private int _allocatedRamMb = 4096;
        private readonly AuthService _authService = new AuthService(Http, ServerBaseUrl);
        private readonly LicenseClient _licenseClient = new LicenseClient(Http, ServerBaseUrl);
        private string _sessionToken = string.Empty;

        // ===== НОВОЕ: настройка "трей / закрытие" + иконка трея =====
        private bool _minimizeToTray = false;
        private System.Windows.Forms.NotifyIcon? _trayIcon;

        private class ProfileSettings
        {
            public int RamMb { get; set; } = 4096;
            public bool MinimizeToTrayOnLaunch { get; set; } = false;
        }

        private readonly ObservableCollection<Friend> _allFriends = new ObservableCollection<Friend>();
        private readonly ObservableCollection<Friend> _visibleFriends = new ObservableCollection<Friend>();

        // ===== МАСТЕРСКАЯ =====
        private readonly ObservableCollection<ModItem> _mods = new ObservableCollection<ModItem>();
        private bool _modsTabInitialized = false;
        private readonly ObservableCollection<InstalledMod> _installedMods = new ObservableCollection<InstalledMod>();
        private bool _installedModsVisible = false;

        // ===== ЗАЩИТА МОДА =====
        private string? _protectedModPath;

        // ===== НОВОЕ: статистика =====
        private string _statsFilePath = string.Empty;
        private LauncherStats _stats = new LauncherStats();

        private class LauncherStats
        {
            public long TotalPlaytimeSeconds { get; set; } = 0;
            public int LaunchCount { get; set; } = 0;
            public DateTime? LastLoginUtc { get; set; }
            public DateTime? LastPlayedUtc { get; set; }
        }

        // ===== НОВОЕ: скины =====
        private string SkinFilePath => Path.Combine(_dataFolder, "skin.png");
        private double _skinRotation = 0;

        // ===== НОВОЕ: новости =====
        private const string ChangelogUrl =
            "https://raw.githubusercontent.com/kryytoi/darkvisualasss/main/changes/Change.txt";
        private const string ChangelogImageUrl =
            "https://raw.githubusercontent.com/kryytoi/darkvisualasss/main/changes/image.png";
        private bool _newsLoaded = false;

        private void WipeProtectedMod()
        {
            if (_protectedModPath == null) return;

            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    if (!File.Exists(_protectedModPath)) return;

                    var fi = new FileInfo(_protectedModPath);
                    byte[] junk = new byte[fi.Length];
                    System.Security.Cryptography.RandomNumberGenerator.Fill(junk);
                    File.WriteAllBytes(_protectedModPath, junk);
                    File.SetAttributes(_protectedModPath, FileAttributes.Normal);
                    File.Delete(_protectedModPath);
                    return;
                }
                catch (IOException) when (attempt < maxAttempts)
                {
                    Thread.Sleep(300);
                }
                catch
                {
                    return;
                }
            }
        }

        public MainWindow()
        {
            System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12 | System.Net.SecurityProtocolType.Tls13;
            InitializeComponent();

            // Плавное появление окна при запуске (fade-in + лёгкий подъём)
            Opacity = 0;
            Loaded += (_, _) =>
            {
                var fadeIn = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.4))
                {
                    EasingFunction = new System.Windows.Media.Animation.QuadraticEase
                    { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                };
                BeginAnimation(OpacityProperty, fadeIn);
            };

            _dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".darkvisuals");
            Directory.CreateDirectory(_dataFolder);
            _friendsFilePath = Path.Combine(_dataFolder, "friends.json");

            _avatarFilePath = Path.Combine(_dataFolder, "avatar.png");
            _profileSettingsPath = Path.Combine(_dataFolder, "profile.json");
            _statsFilePath = Path.Combine(_dataFolder, "stats.json");

            LoadProfileSettings();
            LoadStats();

            FriendsList.ItemsSource = _visibleFriends;
            LoadFriends();

            ModsList.ItemsSource = _mods;
            InstalledModsList.ItemsSource = _installedMods;

            LoadSavedSkin();
            InitTrayIcon();

            _ = CheckLoaderVersionAsync();
        }

        // ===================================================================
        // ПРОВЕРКА ВЕРСИИ ЗАГРУЗЧИКА
        // ===================================================================

        private async Task CheckLoaderVersionAsync()
        {
            try
            {
                // ОБХОД КЭША GitHub: добавляем случайный параметр, чтобы всегда брать свежий файл
                string url = githubVersionUrl + "?t=" + DateTime.UtcNow.Ticks;

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                {
                    NoCache = true,
                    NoStore = true,
                    MustRevalidate = true
                };
                request.Headers.Pragma.ParseAdd("no-cache");

                using var response = await Http.SendAsync(request);
                response.EnsureSuccessStatusCode();

                string remoteVersion = await response.Content.ReadAsStringAsync();

                // Чистим пробелы, переносы строк И невидимый BOM (\uFEFF), который Trim() не убирает
                remoteVersion = remoteVersion.Replace("\uFEFF", "").Trim();

                System.Diagnostics.Debug.WriteLine($"[Version] GitHub='{remoteVersion}', Local='{CurrentLoaderVersion}'");

                if (!string.Equals(remoteVersion, CurrentLoaderVersion, StringComparison.OrdinalIgnoreCase))
                {
                    CurrentVersionText.Text = CurrentLoaderVersion;
                    RequiredVersionText.Text = remoteVersion;

                    BtnPlay.IsEnabled = false;

                    ScreenLoginWrapper.Visibility = Visibility.Collapsed;
                    ScreenUpdateRequired.Visibility = Visibility.Visible;
                }
            }
            catch (Exception ex)
            {
                // Раньше ошибка молча игнорировалась — теперь блокируем вход, чтобы проверку нельзя было обойти
                System.Diagnostics.Debug.WriteLine($"Ошибка проверки версии: {ex.Message}");
                BtnPlay.IsEnabled = false;
                MessageBox.Show(
                    "Не удалось проверить версию лаунчера. Проверьте интернет и перезапустите лаунчер.",
                    "Ошибка проверки версии",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }

        // ===================================================================
        // ОКНО
        // ===================================================================

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left) DragMove();
        }

        private void BtnClose_Click(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        // ===================================================================
        // АВТОРИЗАЦИЯ
        // ===================================================================

        private void BtnLogout_Click(object sender, RoutedEventArgs e)
        {
            LoginBox.Text = "Login";
            PassBox.Password = string.Empty;
            _currentLogin = string.Empty;
            HideLoginError();

            SwitchScreens(MainUI, ScreenLoginWrapper);
        }

        private void LoginBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) PassBox.Focus();
        }

        private async void PassBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) await DoLoginAsync();
        }

        private void PassBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            PassPlaceholder.Visibility = string.IsNullOrEmpty(PassBox.Password) ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void BtnLogin_Click(object sender, RoutedEventArgs e)
        {
            await DoLoginAsync();
        }

        private async Task DoLoginAsync()
        {
            string login = LoginBox.Text.Trim();
            string password = PassBox.Password.Trim();

            HideLoginError();

            if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
            {
                ShowLoginError("Введите логин и пароль!");
                return;
            }

            BtnLogin.IsEnabled = false;
            BtnLogin.Content = "Загрузка...";

            bool isSuccess = false;
            string errorMessage = "Неверный логин или пароль!";

            try
            {
                string hwid = HwidHelper.GetHwid();
                var loginResult = await _authService.LoginAsync(login, password, hwid);

                if (loginResult.Success)
                {
                    isSuccess = true;
                    _currentRole = loginResult.Role;
                    _currentLogin = login;
                    _currentHwid = hwid;
                    _sessionToken = loginResult.SessionToken;
                }
                else
                {
                    errorMessage = loginResult.ErrorMessage;
                }
            }
            catch (Exception)
            {
                errorMessage = "Сервер недоступен.\nПроверьте интернет или попробуйте позже.";
            }

            if (isSuccess)
            {
                ProfileNameText.Text = login;
                ProfileRoleIcon.Text = string.Equals(_currentRole, "Dev", StringComparison.OrdinalIgnoreCase) ? "👾" : "👤";

                ProfilePageNameText.Text = _currentLogin;
                ProfilePageRoleText.Text = _currentRole;

                // НОВОЕ: фиксируем вход + подтягиваем статус лицензии
                RegisterLogin();
                _ = LoadLicenseStatusAsync();

                SwitchScreens(ScreenLoginWrapper, MainUI);
            }
            else
            {
                ShowLoginError(errorMessage);
            }

            BtnLogin.IsEnabled = true;
            BtnLogin.Content = "Войти";
        }

        private void ShowLoginError(string message)
        {
            LoginErrorText.Text = message;
            LoginErrorText.Visibility = Visibility.Visible;
        }

        private void HideLoginError()
        {
            LoginErrorText.Visibility = Visibility.Collapsed;
        }

        // ===================================================================
        // ПЕРЕКЛЮЧЕНИЕ ВКЛАДОК
        // ===================================================================

        private void Menu_Checked(object sender, RoutedEventArgs e)
        {
            if (PageHome == null) return;

            UIElement? targetPage = null;

            if (sender == TabHome) targetPage = PageHome;
            else if (sender == TabVersion) targetPage = PageVersions;
            else if (sender == TabFriends) targetPage = PageFriends;
            else if (sender == TabWorkshop) targetPage = PageWorkshop;
            else if (sender == TabNews) targetPage = PageNews;
            else if (sender == TabProfile) targetPage = PageProfile;
            else if (sender == TabSkins) targetPage = PageSkins;   // НОВОЕ
            else if (sender == TabStats) targetPage = PageStats;   // НОВОЕ

            if (targetPage != null)
            {
                AnimateTabChange(targetPage);
            }
        }

        private void AnimateTabChange(UIElement showElement)
        {
            PageHome.Visibility = Visibility.Collapsed;
            PageVersions.Visibility = Visibility.Collapsed;
            PageFriends.Visibility = Visibility.Collapsed;
            PageWorkshop.Visibility = Visibility.Collapsed;
            PageNews.Visibility = Visibility.Collapsed;
            PageProfile.Visibility = Visibility.Collapsed;
            PageSkins.Visibility = Visibility.Collapsed;   // НОВОЕ
            PageStats.Visibility = Visibility.Collapsed;   // НОВОЕ

            showElement.Visibility = Visibility.Visible;
            showElement.Opacity = 0;

            if (showElement == PageWorkshop && !_modsTabInitialized)
            {
                _modsTabInitialized = true;
                _ = SearchModsAsync(string.Empty);
            }

            // НОВОЕ: ленивая инициализация новых вкладок
            if (showElement == PageNews) _ = LoadNewsAsync();
            if (showElement == PageStats) { RefreshStatsUI(); _ = LoadLicenseStatusAsync(); }
            if (showElement == PageSkins) LoadSavedSkin();

            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            var slideUp = new ThicknessAnimation(new Thickness(0, 12, 0, 0), new Thickness(0, 0, 0, 0), TimeSpan.FromSeconds(0.25))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };

            showElement.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            if (showElement is FrameworkElement fe)
            {
                fe.BeginAnimation(FrameworkElement.MarginProperty, slideUp);
            }
        }

        private void SwitchScreens(UIElement hideElement, UIElement showElement)
        {
            var fadeOut = new DoubleAnimation(1, 0, TimeSpan.FromSeconds(0.2));
            var slideDown = new ThicknessAnimation(new Thickness(0, 0, 0, 0), new Thickness(0, 20, 0, 0), TimeSpan.FromSeconds(0.2))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) =>
            {
                hideElement.Visibility = Visibility.Collapsed;
                showElement.Visibility = Visibility.Visible;

                var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.35))
                {
                    EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
                };
                var slideUp = new ThicknessAnimation(new Thickness(0, 30, 0, 0), new Thickness(0, 0, 0, 0), TimeSpan.FromSeconds(0.35))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.4 }
                };

                showElement.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                if (showElement is FrameworkElement showFe)
                {
                    showFe.BeginAnimation(FrameworkElement.MarginProperty, slideUp);
                }
            };

            hideElement.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            if (hideElement is FrameworkElement hideFe)
            {
                hideFe.BeginAnimation(FrameworkElement.MarginProperty, slideDown);
            }
        }

        // ===================================================================
        // РАЗДЕЛ "ДРУЗЬЯ"
        // ===================================================================

        private void LoadFriends()
        {
            try
            {
                if (File.Exists(_friendsFilePath))
                {
                    string json = File.ReadAllText(_friendsFilePath);
                    var names = JsonSerializer.Deserialize<string[]>(json) ?? Array.Empty<string>();
                    foreach (var name in names)
                    {
                        _allFriends.Add(new Friend { Nickname = name });
                    }
                }
            }
            catch
            {
            }

            RefreshVisibleFriends();
        }

        private void SaveFriends()
        {
            try
            {
                var names = _allFriends.Select(f => f.Nickname).ToArray();
                string json = JsonSerializer.Serialize(names);
                File.WriteAllText(_friendsFilePath, json);
            }
            catch
            {
            }
        }

        private void RefreshVisibleFriends()
        {
            string filter = FriendSearchBox?.Text?.Trim() ?? string.Empty;

            _visibleFriends.Clear();
            foreach (var friend in _allFriends)
            {
                if (filter.Length == 0 || friend.Nickname.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    _visibleFriends.Add(friend);
                }
            }

            if (NoFriendsText != null)
            {
                NoFriendsText.Visibility = _allFriends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void FriendSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshVisibleFriends();
        }

        private void NewFriendBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) AddFriend_Click(sender, e);
        }

        private void AddFriend_Click(object sender, RoutedEventArgs e)
        {
            string nickname = NewFriendBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(nickname)) return;

            if (_allFriends.Any(f => f.Nickname.Equals(nickname, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("Такой друг уже есть в списке!", "Друзья", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _allFriends.Add(new Friend { Nickname = nickname });
            NewFriendBox.Text = string.Empty;

            SaveFriends();
            RefreshVisibleFriends();
        }

        private void RemoveFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is Friend friend)
            {
                var result = MessageBox.Show($"Удалить \"{friend.Nickname}\" из друзей?", "Подтверждение",
                    MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _allFriends.Remove(friend);
                    SaveFriends();
                    RefreshVisibleFriends();
                }
            }
        }

        private void EditFriend_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is Friend friend)
            {
                NewFriendBox.Text = friend.Nickname;
                NewFriendBox.Focus();
                NewFriendBox.SelectAll();

                _allFriends.Remove(friend);
                SaveFriends();
                RefreshVisibleFriends();
            }
        }

        // ===================================================================
        // РАЗДЕЛ "МАСТЕРСКАЯ": Modrinth
        // ===================================================================

        private const string ModrinthApiBase = "https://api.modrinth.com/v2";

        private async void BtnSearchMods_Click(object sender, RoutedEventArgs e)
        {
            await SearchModsAsync(ModSearchBox.Text.Trim());
        }

        private async void ModSearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                await SearchModsAsync(ModSearchBox.Text.Trim());
        }

        private async Task SearchModsAsync(string query)
        {
            ModsStatusText.Text = "Поиск...";
            NoModsText.Visibility = Visibility.Collapsed;

            try
            {
                string facets = "[[\"project_type:mod\"],[\"categories:fabric\"],[\"versions:1.21.4\"]]";
                string url = $"{ModrinthApiBase}/search?query={Uri.EscapeDataString(query)}&limit=30&facets={Uri.EscapeDataString(facets)}";

                var result = await Http.GetFromJsonAsync<ModrinthSearchResponse>(url);

                _mods.Clear();
                if (result?.Hits != null)
                {
                    foreach (var hit in result.Hits)
                    {
                        _mods.Add(new ModItem
                        {
                            ProjectId = !string.IsNullOrWhiteSpace(hit.ProjectId) ? hit.ProjectId! : (hit.Slug ?? string.Empty),
                            Title = hit.Title ?? "Без названия",
                            Description = hit.Description ?? string.Empty,
                            IconUrl = hit.IconUrl ?? string.Empty,
                            Downloads = hit.Downloads
                        });
                    }
                }

                ModsStatusText.Text = _mods.Count > 0 ? $"Найдено: {_mods.Count}" : "Ничего не найдено";
                NoModsText.Visibility = _mods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                string details = ex.Message;
                var inner = ex.InnerException;
                while (inner != null)
                {
                    details += $"\n→ {inner.Message}";
                    inner = inner.InnerException;
                }

                MessageBox.Show($"Ошибка во время загрузки игры:\n{details}", "Краш", MessageBoxButton.OK, MessageBoxImage.Error);
                SwitchScreens(ScreenDownload, PageHome);
            }
        }

        private async void BtnDownloadMod_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn || btn.Tag is not ModItem mod) return;

            mod.IsEnabled = false;
            mod.StatusText = "Загрузка...";

            try
            {
                string versionsUrl = $"{ModrinthApiBase}/project/{mod.ProjectId}/version?loaders=[\"fabric\"]&game_versions=[\"1.21.4\"]";
                var versions = await Http.GetFromJsonAsync<List<ModrinthVersion>>(versionsUrl);

                var file = versions?.FirstOrDefault()?.Files?.FirstOrDefault(f => f.Primary)
                           ?? versions?.FirstOrDefault()?.Files?.FirstOrDefault();

                if (file == null || string.IsNullOrWhiteSpace(file.Url))
                {
                    mod.StatusText = "Нет файла для 1.21.4";
                    return;
                }

                string modsFolder = Path.Combine(_dataFolder, "mods");
                Directory.CreateDirectory(modsFolder);

                byte[] bytes = await Http.GetByteArrayAsync(file.Url);
                string fileName = string.IsNullOrWhiteSpace(file.Filename) ? $"{mod.ProjectId}.jar" : file.Filename!;
                await File.WriteAllBytesAsync(Path.Combine(modsFolder, fileName), bytes);

                mod.StatusText = "Установлено ✓";
            }
            catch (Exception ex)
            {
                mod.StatusText = "Ошибка";
                MessageBox.Show($"Не удалось скачать мод «{mod.Title}»:\n{ex.Message}", "Мастерская", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                if (mod.StatusText != "Установлено ✓") mod.IsEnabled = true;
            }
        }

        // ===================================================================
        // УСТАНОВЛЕННЫЕ МОДЫ
        // ===================================================================

        private void BtnInstalledMods_Click(object sender, RoutedEventArgs e)
        {
            _installedModsVisible = !_installedModsVisible;

            if (_installedModsVisible)
            {
                LoadInstalledMods();
                SearchModsPanel.Visibility = Visibility.Collapsed;
                InstalledModsPanel.Visibility = Visibility.Visible;
                BtnInstalledMods.Content = "🔍 К поиску";
            }
            else
            {
                InstalledModsPanel.Visibility = Visibility.Collapsed;
                SearchModsPanel.Visibility = Visibility.Visible;
                BtnInstalledMods.Content = "📦 Установленные";
            }
        }

        private void LoadInstalledMods()
        {
            _installedMods.Clear();

            try
            {
                string modsFolder = Path.Combine(_dataFolder, "mods");
                if (Directory.Exists(modsFolder))
                {
                    foreach (var filePath in Directory.GetFiles(modsFolder, "*.jar"))
                    {
                        string fileName = Path.GetFileName(filePath);

                        if (fileName.Equals("darkvisuals.jar", StringComparison.OrdinalIgnoreCase))
                            continue;

                        _installedMods.Add(new InstalledMod
                        {
                            FileName = fileName,
                            FilePath = filePath
                        });
                    }
                }
            }
            catch { }

            NoInstalledModsText.Visibility = _installedMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ModsStatusText.Text = _installedModsVisible ? $"Установлено: {_installedMods.Count}" : ModsStatusText.Text;
        }

        private void RemoveInstalledMod_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not InstalledMod mod) return;

            var result = MessageBox.Show($"Удалить мод «{mod.FileName}»?", "Удаление мода",
                MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                if (File.Exists(mod.FilePath))
                    File.Delete(mod.FilePath);

                _installedMods.Remove(mod);
                NoInstalledModsText.Visibility = _installedMods.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ModsStatusText.Text = $"Установлено: {_installedMods.Count}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось удалить мод:\n{ex.Message}", "Мастерская", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private class ModrinthSearchResponse
        {
            [JsonPropertyName("hits")]
            public List<ModrinthHit>? Hits { get; set; }
        }

        private class ModrinthHit
        {
            [JsonPropertyName("project_id")]
            public string? ProjectId { get; set; }

            [JsonPropertyName("slug")]
            public string? Slug { get; set; }

            [JsonPropertyName("title")]
            public string? Title { get; set; }

            [JsonPropertyName("description")]
            public string? Description { get; set; }

            [JsonPropertyName("icon_url")]
            public string? IconUrl { get; set; }

            [JsonPropertyName("downloads")]
            public long Downloads { get; set; }
        }

        private class ModrinthVersion
        {
            [JsonPropertyName("files")]
            public List<ModrinthFile>? Files { get; set; }
        }

        private class ModrinthFile
        {
            [JsonPropertyName("url")]
            public string? Url { get; set; }

            [JsonPropertyName("filename")]
            public string? Filename { get; set; }

            [JsonPropertyName("primary")]
            public bool Primary { get; set; }
        }

        // ===================================================================
        // ЗАГРУЗКА И ЗАПУСК ИГРЫ
        // ===================================================================

        private async void BtnLaunchFabric_Click(object sender, RoutedEventArgs e)
        {
            SwitchScreens(PageHome, ScreenDownload);

            try
            {
                string mcPath = _dataFolder;
                string mcVersion = "1.21.4";
                string fabricLoaderVersion = "0.18.0";
                string targetVersionName = $"fabric-loader-{fabricLoaderVersion}-{mcVersion}";

                var path = new MinecraftPath(mcPath);
                var launcher = new MinecraftLauncher(path);

                LoadingText.Text = "Установка движка...";
                CurrentFileText.Text = "Fabric Loader";
                await InstallFabricAsync(mcPath, mcVersion, fabricLoaderVersion);

                LoadingText.Text = "Установка модификаций...";
                await DownloadModsAsync(mcPath);

                launcher.FileProgressChanged += (s, ev) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        string fileName = ev.Name ?? "Загрузка...";
                        CurrentFileText.Text = fileName.Length > 25 ? fileName.Substring(0, 25) + "..." : fileName;

                        if (ev.TotalTasks > 0)
                        {
                            int percentage = (int)(((double)ev.ProgressedTasks / ev.TotalTasks) * 100);
                            if (percentage > 100) percentage = 100;
                            ProgressPercentText.Text = $"{percentage} %";

                            double maxWidth = ProgressBarFill.Parent is Grid parentGrid ? parentGrid.ActualWidth : 250;
                            ProgressBarFill.Width = (maxWidth * percentage) / 100;
                        }
                    });
                };

                var launchOptions = new MLaunchOption
                {
                    MaximumRamMb = _allocatedRamMb,
                    Session = MSession.CreateOfflineSession(LoginBox.Text)
                };

                LoadingText.Text = "Загрузка ресурсов Minecraft...";
                CurrentFileText.Text = "Проверка файлов...";

                await launcher.InstallAsync(targetVersionName);

                LoadingText.Text = "Готово!";
                CurrentFileText.Text = "Запуск игры...";

                

                var process = await launcher.CreateProcessAsync(targetVersionName, launchOptions);
                process.EnableRaisingEvents = true;
                process.Start();

                // НОВОЕ: статистика запуска + засекаем время сессии
                RegisterLaunch();
                var sessionStart = DateTime.UtcNow;

                // НОВОЕ: поведение согласно настройке (трей или закрытие)
                Dispatcher.Invoke(() =>
                {
                    SwitchScreens(ScreenDownload, PageHome);
                    if (_minimizeToTray) HideToTray();
                });

                _ = Task.Run(async () =>
                {
                    await process.WaitForExitAsync();
                    WipeProtectedMod();

                    var played = DateTime.UtcNow - sessionStart;
                    Dispatcher.Invoke(() => AddPlaytime(played));

                    Dispatcher.Invoke(() =>
                    {
                        if (_minimizeToTray)
                            RestoreFromTray();
                        else
                            Application.Current.Shutdown();
                    });
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка во время загрузки игры:\n{ex.Message}", "Краш", MessageBoxButton.OK, MessageBoxImage.Error);
                SwitchScreens(ScreenDownload, PageHome);
            }
        }

        private void BtnCancelDownload_Click(object sender, RoutedEventArgs e)
        {
            SwitchScreens(ScreenDownload, PageHome);
        }

        private async Task DownloadModsAsync(string mcPath)
        {
            string modsFolder = Path.Combine(mcPath, "mods");
            Directory.CreateDirectory(modsFolder);

            var result = await _licenseClient.DownloadProtectedModAsync(
                _currentLogin, _currentHwid, _sessionToken,
                modsFolder, mcPath,
                status => CurrentFileText.Text = status);

            _protectedModPath = result.JarPath;

            await _licenseClient.DownloadFabricApiAsync(modsFolder, status => CurrentFileText.Text = status);
        }

        private class KeyResponse
        {
            [JsonPropertyName("KeyBase64")]
            public string KeyBase64 { get; set; } = "";

            [JsonPropertyName("IvBase64")]
            public string IvBase64 { get; set; } = "";

            [JsonPropertyName("ModUrl")]
            public string ModUrl { get; set; } = "";
        }

        private class ErrorResponse
        {
            [JsonPropertyName("error")]
            public string? Error { get; set; }
        }

        private static byte[] DecryptAes(byte[] data, byte[] key, byte[] iv)
        {
            if (data == null || data.Length == 0)
                throw new InvalidDataException("Зашифрованный файл мода пуст (0 байт) — похоже, скачивание не удалось.");

            if (data.Length % 16 != 0)
                throw new InvalidDataException(
                    $"Зашифрованный файл мода повреждён или скачан не полностью (размер {data.Length} байт не кратен 16). " +
                    "Скорее всего, обрыв соединения при загрузке.");

            if (key == null || (key.Length != 16 && key.Length != 24 && key.Length != 32))
                throw new InvalidDataException(
                    $"Некорректный AES-ключ от сервера лицензий (длина {key?.Length ?? 0} байт, ожидалось 16/24/32).");

            if (iv == null || iv.Length != 16)
                throw new InvalidDataException(
                    $"Некорректный IV от сервера лицензий (длина {iv?.Length ?? 0} байт, ожидалось 16).");

            try
            {
                using var aes = Aes.Create();
                aes.Key = key; aes.IV = iv;
                using var dec = aes.CreateDecryptor();
                return dec.TransformFinalBlock(data, 0, data.Length);
            }
            catch (CryptographicException)
            {
                throw new InvalidDataException(
                    "Не удалось расшифровать файл мода: ключ/IV не подходят к скачанному файлу. " +
                    "Похоже на рассинхрон между сервером лицензий и файлом darkvisuals.enc " +
                    "(например, .enc обновили, а ключ выдаётся под старую версию), либо файл повреждён при скачивании.");
            }
        }

        private async Task InstallFabricAsync(string mcPath, string mcVersion, string loaderVersion)
        {
            string versionName = $"fabric-loader-{loaderVersion}-{mcVersion}";
            string versionFolder = Path.Combine(mcPath, "versions", versionName);
            Directory.CreateDirectory(versionFolder);

            string jsonFilePath = Path.Combine(versionFolder, $"{versionName}.json");

            if (IsValidFabricProfileJson(jsonFilePath))
                return;

            string fabricApiUrl = $"https://meta.fabricmc.net/v2/versions/loader/{mcVersion}/{loaderVersion}/profile/json";
            string jsonContent = await Http.GetStringAsync(fabricApiUrl);

            string tempPath = jsonFilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, jsonContent);
            File.Move(tempPath, jsonFilePath, overwrite: true);
        }

        private static bool IsValidFabricProfileJson(string path)
        {
            if (!File.Exists(path))
                return false;

            try
            {
                var info = new FileInfo(path);
                if (info.Length == 0)
                    return false;

                using var doc = JsonDocument.Parse(File.ReadAllText(path));
                var root = doc.RootElement;

                bool hasInheritsFrom = root.TryGetProperty("inheritsFrom", out _);
                bool hasMainClass = root.TryGetProperty("mainClass", out var mainClassEl)
                    && mainClassEl.GetString()?.Contains("fabricmc", StringComparison.OrdinalIgnoreCase) == true;

                return hasInheritsFrom && hasMainClass;
            }
            catch (JsonException)
            {
                return false;
            }
            catch (IOException)
            {
                return false;
            }
        }

        private static byte[] AddWatermark(byte[] jar, string mark)
        {
            byte[] comment = System.Text.Encoding.UTF8.GetBytes("DV:" + mark);
            if (comment.Length > ushort.MaxValue) return jar;

            int eocd = -1;
            int stop = Math.Max(0, jar.Length - 22 - 65535);
            for (int i = jar.Length - 22; i >= stop; i--)
            {
                if (jar[i] == 0x50 && jar[i + 1] == 0x4B && jar[i + 2] == 0x05 && jar[i + 3] == 0x06)
                {
                    eocd = i;
                    break;
                }
            }
            if (eocd < 0) return jar;

            int endOfEocd = eocd + 22;

            var result = new byte[endOfEocd + comment.Length];
            Buffer.BlockCopy(jar, 0, result, 0, endOfEocd);

            result[eocd + 20] = (byte)(comment.Length & 0xFF);
            result[eocd + 21] = (byte)((comment.Length >> 8) & 0xFF);

            Buffer.BlockCopy(comment, 0, result, endOfEocd, comment.Length);
            return result;
        }

        public class InstalledMod : INotifyPropertyChanged
        {
            private string _fileName = string.Empty;
            public string FileName
            {
                get => _fileName;
                set { _fileName = value; OnPropertyChanged(); }
            }

            public string FilePath { get; set; } = string.Empty;

            private bool _isDisabled;
            public bool IsDisabled
            {
                get => _isDisabled;
                set
                {
                    _isDisabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(ToggleButtonText));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }

            public string StatusText => IsDisabled ? "Отключён (.off)" : "Включён (.jar)";
            public string ToggleButtonText => IsDisabled ? "Включить" : "Отключить";
            public string StatusColor => IsDisabled ? "#888888" : "#4CAF50";

            public event PropertyChangedEventHandler? PropertyChanged;
            protected void OnPropertyChanged([CallerMemberName] string? name = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
            }
        }

        // ===================================================================
        // ПРОФИЛЬ: аватарка и ОЗУ
        // ===================================================================

        private void LoadProfileSettings()
        {
            try
            {
                if (File.Exists(_profileSettingsPath))
                {
                    var settings = System.Text.Json.JsonSerializer.Deserialize<ProfileSettings>(File.ReadAllText(_profileSettingsPath));
                    if (settings != null)
                    {
                        _allocatedRamMb = Math.Clamp(settings.RamMb, 2048, 16384);
                        _minimizeToTray = settings.MinimizeToTrayOnLaunch;   // НОВОЕ
                    }
                }
            }
            catch { }

            RamSlider.Value = _allocatedRamMb;
            UpdateRamText();

            if (TrayToggle != null) TrayToggle.IsChecked = _minimizeToTray;   // НОВОЕ

            LoadAvatarImage();
        }

        private void SaveProfileSettings()
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new ProfileSettings
                {
                    RamMb = _allocatedRamMb,
                    MinimizeToTrayOnLaunch = _minimizeToTray   // НОВОЕ
                });
                File.WriteAllText(_profileSettingsPath, json);
            }
            catch { }
        }

        private void LoadAvatarImage()
        {
            try
            {
                if (!File.Exists(_avatarFilePath)) return;

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(_avatarFilePath);
                bitmap.EndInit();
                bitmap.Freeze();

                ProfileAvatarBrush.ImageSource = bitmap;
                SidebarAvatarBrush.ImageSource = bitmap;
                ProfileAvatarLetter.Visibility = Visibility.Collapsed;
                SidebarAvatarLetter.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void BtnChangeAvatar_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите аватарку",
                Filter = "Картинки (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                File.Copy(dialog.FileName, _avatarFilePath, overwrite: true);
                LoadAvatarImage();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось установить аватарку:\n{ex.Message}", "Профиль", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void RamSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (RamValueText == null) return;

            _allocatedRamMb = (int)RamSlider.Value;
            UpdateRamText();
            SaveProfileSettings();
        }

        private void UpdateRamText()
        {
            double gb = _allocatedRamMb / 1024.0;
            RamValueText.Text = $"{_allocatedRamMb} МБ ({gb:0.#} ГБ)";
        }

        // ===================================================================
        // НОВОЕ: НАСТРОЙКИ (трей / папка игры / кэш)
        // ===================================================================

        private void TrayToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (TrayToggle == null) return;
            _minimizeToTray = TrayToggle.IsChecked == true;
            SaveProfileSettings();
        }

        private void BtnOpenGameFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_dataFolder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _dataFolder,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось открыть папку:\n{ex.Message}", "Папка игры",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnClearCache_Click(object sender, RoutedEventArgs e)
        {
            var confirm = MessageBox.Show(
                "Удалить кэш Minecraft (assets, libraries, versions)?\n" +
                "Моды, друзья и настройки профиля останутся. При следующем запуске файлы скачаются заново.",
                "Очистка кэша", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;

            string[] cacheDirs = { "assets", "libraries", "versions", "logs", "crash-reports" };
            long freed = 0;
            var errors = new List<string>();

            foreach (var name in cacheDirs)
            {
                string dir = Path.Combine(_dataFolder, name);
                if (!Directory.Exists(dir)) continue;

                try
                {
                    freed += GetDirSize(dir);
                    Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex)
                {
                    errors.Add($"{name}: {ex.Message}");
                }
            }

            string mb = (freed / 1024.0 / 1024.0).ToString("0.#");
            if (errors.Count == 0)
                MessageBox.Show($"Кэш очищен. Освобождено ~{mb} МБ.", "Очистка кэша",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show($"Очищено ~{mb} МБ, но часть файлов занята:\n{string.Join("\n", errors)}",
                    "Очистка кэша", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private static long GetDirSize(string path)
        {
            try
            {
                return new DirectoryInfo(path)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => { try { return f.Length; } catch { return 0; } });
            }
            catch { return 0; }
        }

        // ===================================================================
        // НОВОЕ: ТРЕЙ
        // ===================================================================

        private void InitTrayIcon()
        {
            try
            {
                _trayIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(
                               System.Reflection.Assembly.GetExecutingAssembly().Location)
                           ?? System.Drawing.SystemIcons.Application,
                    Visible = false,
                    Text = "DarkVisuals — идёт игра"
                };

                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Открыть лаунчер", null, (_, __) => RestoreFromTray());
                menu.Items.Add("Выход", null, (_, __) => Application.Current.Shutdown());
                _trayIcon.ContextMenuStrip = menu;
                _trayIcon.DoubleClick += (_, __) => RestoreFromTray();
            }
            catch { _trayIcon = null; }
        }

        private void HideToTray()
        {
            if (_trayIcon == null) { WindowState = WindowState.Minimized; return; }
            _trayIcon.Visible = true;
            Hide();
            _trayIcon.ShowBalloonTip(2000, "DarkVisuals", "Лаунчер свёрнут в трей. Игра запущена.",
                System.Windows.Forms.ToolTipIcon.Info);
        }

        private void RestoreFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            if (_trayIcon != null) _trayIcon.Visible = false;
        }

        // ===================================================================
        // НОВОЕ: СТАТИСТИКА
        // ===================================================================

        private void LoadStats()
        {
            try
            {
                if (File.Exists(_statsFilePath))
                    _stats = System.Text.Json.JsonSerializer.Deserialize<LauncherStats>(
                                 File.ReadAllText(_statsFilePath)) ?? new LauncherStats();
            }
            catch { _stats = new LauncherStats(); }
        }

        private void SaveStats()
        {
            try
            {
                File.WriteAllText(_statsFilePath,
                    System.Text.Json.JsonSerializer.Serialize(_stats));
            }
            catch { }
        }

        private void RegisterLogin()
        {
            _stats.LastLoginUtc = DateTime.UtcNow;
            SaveStats();
        }

        private void RegisterLaunch()
        {
            _stats.LaunchCount++;
            _stats.LastPlayedUtc = DateTime.UtcNow;
            SaveStats();
        }

        private void AddPlaytime(TimeSpan played)
        {
            if (played.TotalSeconds > 0)
                _stats.TotalPlaytimeSeconds += (long)played.TotalSeconds;
            SaveStats();
            RefreshStatsUI();
        }

        private void RefreshStatsUI()
        {
            if (StatPlaytimeText == null) return;

            var t = TimeSpan.FromSeconds(_stats.TotalPlaytimeSeconds);
            StatPlaytimeText.Text = t.TotalHours >= 1
                ? $"{(int)t.TotalHours} ч {t.Minutes} мин"
                : $"{t.Minutes} мин";

            StatLaunchCountText.Text = _stats.LaunchCount.ToString();

            StatLastLoginText.Text = _stats.LastLoginUtc.HasValue
                ? _stats.LastLoginUtc.Value.ToLocalTime().ToString("dd.MM.yyyy HH:mm")
                : "—";
        }

        private async Task LoadLicenseStatusAsync()
        {
            if (StatLicenseText == null) return;

            StatLicenseText.Text = "Проверка…";
            StatLicenseText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

            try
            {
                var resp = await Http.PostAsJsonAsync($"{ServerBaseUrl}/api/verify",
                    new { login = _currentLogin, hwid = _currentHwid, sessionToken = _sessionToken });

                if (!resp.IsSuccessStatusCode)
                {
                    StatLicenseText.Text = "Не подтверждена";
                    StatLicenseText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
                    return;
                }

                var info = await resp.Content.ReadFromJsonAsync<VerifyResponse>();
                if (info != null && info.Valid)
                {
                    if (info.ExpiresAt.HasValue)
                    {
                        var left = info.ExpiresAt.Value.ToUniversalTime() - DateTime.UtcNow;
                        StatLicenseText.Text = left.TotalDays >= 1
                            ? $"Активна · до {info.ExpiresAt.Value.ToLocalTime():dd.MM.yyyy} ({(int)left.TotalDays} дн.)"
                            : "Активна · истекает сегодня";
                    }
                    else
                    {
                        StatLicenseText.Text = "Активна · бессрочно";
                    }
                    StatLicenseText.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
                }
                else
                {
                    StatLicenseText.Text = "Истекла / не активна";
                    StatLicenseText.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x5A, 0x5A));
                }
            }
            catch
            {
                StatLicenseText.Text = "Сервер недоступен";
                StatLicenseText.Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
            }
        }

        private sealed class VerifyResponse
        {
            [JsonPropertyName("valid")]
            public bool Valid { get; set; }

            [JsonPropertyName("expiresAt")]
            public DateTime? ExpiresAt { get; set; }

            [JsonPropertyName("plan")]
            public string? Plan { get; set; }
        }

        // ===================================================================
        // НОВОЕ: СКИНЫ (оффлайн-превью)
        // ===================================================================

        private void LoadSavedSkin()
        {
            if (File.Exists(SkinFilePath))
                ShowSkinPreview(SkinFilePath);
        }

        private void BtnLoadSkin_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Выберите скин (PNG 64x64 или 64x32)",
                Filter = "Скин Minecraft (*.png)|*.png"
            };
            if (dialog.ShowDialog() != true) return;

            try
            {
                var test = new BitmapImage();
                test.BeginInit();
                test.CacheOption = BitmapCacheOption.OnLoad;
                test.UriSource = new Uri(dialog.FileName);
                test.EndInit();

                if (!(test.PixelWidth == 64 && (test.PixelHeight == 64 || test.PixelHeight == 32)))
                {
                    MessageBox.Show("Ожидается скин 64x64 (или старый 64x32).", "Скины",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                File.Copy(dialog.FileName, SkinFilePath, overwrite: true);
                ShowSkinPreview(SkinFilePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Не удалось загрузить скин:\n{ex.Message}", "Скины",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void ShowSkinPreview(string path)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();

                if (SkinFlatImage != null) SkinFlatImage.Source = bmp;
                if (SkinModelVisual != null) SkinModelVisual.Content = SkinModel3D.Build(bmp);
                if (SkinNoText != null) SkinNoText.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка отрисовки превью:\n{ex.Message}", "Скины",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void BtnRotateSkin_Click(object sender, RoutedEventArgs e)
        {
            _skinRotation += 45;
            if (SkinRotateTransform != null) SkinRotateTransform.Angle = _skinRotation;
        }

        // ===================================================================
        // НОВОЕ: НОВОСТИ (changelog + картинка с GitHub)
        // ===================================================================

        private async Task LoadNewsAsync()
        {
            if (_newsLoaded) return;

            NewsVersionText.Text = "Загрузка…";
            NewsBodyText.Text = string.Empty;

            try
            {
                string raw = await Http.GetStringAsync(ChangelogUrl);
                var lines = raw.Replace("\r\n", "\n").Split('\n');

                string version = lines.Length > 0 ? lines[0].Trim() : "";
                string body = lines.Length > 1
                    ? string.Join(Environment.NewLine, lines.Skip(1).Select(l => l.TrimEnd())).Trim()
                    : "";

                NewsVersionText.Text = string.IsNullOrWhiteSpace(version) ? "Обновление" : $"Обновление {version}";
                NewsBodyText.Text = string.IsNullOrWhiteSpace(body) ? "Описание отсутствует." : body;

                _newsLoaded = true;
            }
            catch (Exception ex)
            {
                NewsVersionText.Text = "Не удалось загрузить новости";
                NewsBodyText.Text = ex.Message;
            }

            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(ChangelogImageUrl);
                bmp.EndInit();
                NewsImage.Source = bmp;
                NewsImage.Visibility = Visibility.Visible;
            }
            catch
            {
                NewsImage.Visibility = Visibility.Collapsed;
            }
        }
    }

    // ===================================================================
    // НОВОЕ: построение 3D-модели скина для Viewport3D
    // ===================================================================
    internal static class SkinModel3D
    {
        private const double U = 1.0 / 16.0;

        public static Model3DGroup Build(BitmapSource skin)
        {
            var brush = new ImageBrush(skin)
            {
                ViewportUnits = BrushMappingMode.Absolute,
                Stretch = Stretch.Fill,
                TileMode = TileMode.None
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);

            double texW = skin.PixelWidth;
            double texH = skin.PixelHeight;

            var group = new Model3DGroup();

            // HEAD
            AddBox(group, brush, texW, texH, 8, 8, 8, 0, 10 * U, 0, 8, 8, 8, 8);
            // BODY
            AddBox(group, brush, texW, texH, 8, 12, 4, 0, 2 * U, 0, 16, 16, 8, 12);
            // ARM RIGHT
            AddBox(group, brush, texW, texH, 4, 12, 4, -6 * U, 2 * U, 0, 40, 16, 4, 12);
            // ARM LEFT
            AddBox(group, brush, texW, texH, 4, 12, 4, 6 * U, 2 * U, 0, 32, 48, 4, 12);
            // LEG RIGHT
            AddBox(group, brush, texW, texH, 4, 12, 4, -2 * U, -10 * U, 0, 0, 16, 4, 12);
            // LEG LEFT
            AddBox(group, brush, texW, texH, 4, 12, 4, 2 * U, -10 * U, 0, 16, 48, 4, 12);

            group.Freeze();
            return group;
        }

        private static void AddBox(Model3DGroup group, ImageBrush brush,
            double texW, double texH,
            double wPx, double hPx, double dPx,
            double cx, double cy, double cz,
            double uOff, double vOff, double wFace, double hFace)
        {
            double w = wPx * U, h = hPx * U, d = dPx * U;
            double x0 = cx - w / 2, x1 = cx + w / 2;
            double y0 = cy - h / 2, y1 = cy + h / 2;
            double z0 = cz - d / 2, z1 = cz + d / 2;

            var mesh = new MeshGeometry3D();

            // Front (+Z)
            AddQuad(mesh, texW, texH,
                new Point3D(x0, y0, z1), new Point3D(x1, y0, z1),
                new Point3D(x1, y1, z1), new Point3D(x0, y1, z1),
                uOff + dPx, vOff + dPx, wFace, hFace);

            // Back (-Z)
            AddQuad(mesh, texW, texH,
                new Point3D(x1, y0, z0), new Point3D(x0, y0, z0),
                new Point3D(x0, y1, z0), new Point3D(x1, y1, z0),
                uOff + dPx * 2 + wFace, vOff + dPx, wFace, hFace);

            // Right (-X)
            AddQuad(mesh, texW, texH,
                new Point3D(x0, y0, z0), new Point3D(x0, y0, z1),
                new Point3D(x0, y1, z1), new Point3D(x0, y1, z0),
                uOff, vOff + dPx, dPx, hFace);

            // Left (+X)
            AddQuad(mesh, texW, texH,
                new Point3D(x1, y0, z1), new Point3D(x1, y0, z0),
                new Point3D(x1, y1, z0), new Point3D(x1, y1, z1),
                uOff + dPx + wFace, vOff + dPx, dPx, hFace);

            // Top (+Y)
            AddQuad(mesh, texW, texH,
                new Point3D(x0, y1, z1), new Point3D(x1, y1, z1),
                new Point3D(x1, y1, z0), new Point3D(x0, y1, z0),
                uOff + dPx, vOff, wFace, dPx);

            // Bottom (-Y)
            AddQuad(mesh, texW, texH,
                new Point3D(x0, y0, z0), new Point3D(x1, y0, z0),
                new Point3D(x1, y0, z1), new Point3D(x0, y0, z1),
                uOff + dPx + wFace, vOff, wFace, dPx);

            var material = new DiffuseMaterial(brush);
            var model = new GeometryModel3D(mesh, material) { BackMaterial = material };
            group.Children.Add(model);
        }

        private static void AddQuad(MeshGeometry3D mesh, double texW, double texH,
            Point3D p0, Point3D p1, Point3D p2, Point3D p3,
            double u, double v, double uw, double vh)
        {
            int i = mesh.Positions.Count;
            mesh.Positions.Add(p0); mesh.Positions.Add(p1);
            mesh.Positions.Add(p2); mesh.Positions.Add(p3);

            double uL = u / texW, uR = (u + uw) / texW;
            double vT = v / texH, vB = (v + vh) / texH;

            mesh.TextureCoordinates.Add(new System.Windows.Point(uL, vB));
            mesh.TextureCoordinates.Add(new System.Windows.Point(uR, vB));
            mesh.TextureCoordinates.Add(new System.Windows.Point(uR, vT));
            mesh.TextureCoordinates.Add(new System.Windows.Point(uL, vT));

            mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 1); mesh.TriangleIndices.Add(i + 2);
            mesh.TriangleIndices.Add(i); mesh.TriangleIndices.Add(i + 2); mesh.TriangleIndices.Add(i + 3);
        }
    }
}