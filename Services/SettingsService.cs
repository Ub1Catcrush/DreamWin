using DreamWin.Models;

namespace DreamWin.Services;

public class SettingsService
{
    private AppSettings _settings;

    public AppSettings Settings => _settings;
    public event EventHandler<ReceiverConfig?>? ActiveReceiverChanged;

    public SettingsService()
    {
        _settings = AppSettings.Load();
    }

    public ReceiverConfig? GetActiveReceiver()
    {
        if (_settings.ActiveReceiverId.HasValue)
            return _settings.Receivers.FirstOrDefault(r => r.Id == _settings.ActiveReceiverId);
        return _settings.Receivers.FirstOrDefault();
    }

    public void SetActiveReceiver(ReceiverConfig config)
    {
        _settings.ActiveReceiverId = config.Id;
        Save();
        ActiveReceiverChanged?.Invoke(this, config);
    }

    public void AddReceiver(ReceiverConfig config)
    {
        _settings.Receivers.Add(config);
        if (_settings.Receivers.Count == 1)
            _settings.ActiveReceiverId = config.Id;
        Save();
    }

    public void UpdateReceiver(ReceiverConfig config)
    {
        var idx = _settings.Receivers.FindIndex(r => r.Id == config.Id);
        if (idx >= 0) _settings.Receivers[idx] = config;
        Save();
    }

    public void RemoveReceiver(ReceiverConfig config)
    {
        _settings.Receivers.Remove(config);
        if (_settings.ActiveReceiverId == config.Id)
            _settings.ActiveReceiverId = _settings.Receivers.FirstOrDefault()?.Id;
        Save();
    }

    public void Save() => _settings.Save();
}
