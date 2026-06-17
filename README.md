# DreamWin 📺

**Windows-Client für Enigma2 Sat-Receiver** (DreamBox, VU+, Gigablue, Xtrend, ...)  
Funktional vergleichbar mit DreamDroid für Android.

---

## Features

| Feature | Details |
|---|---|
| 📺 Live TV | Bouquet-Browser, Senderliste, HLS/HTTP Streaming via libVLC |
| 📅 EPG Guide | Jetzt/Nächstes, 48h-Vorschau, EPG-Suche |
| ⏺ Timer | Timer ansehen, anlegen (aus EPG), aktivieren/deaktivieren, löschen |
| 🎬 Recordings | Aufnahmen wiedergeben, löschen |
| ⚙ Receiver | Mehrere Receiver konfigurieren, per Klick wechseln |
| 🎨 UI | Dark-Theme, frameless Window, libVLC-Player integriert |

---

## Voraussetzungen

- **Windows 10/11** (x64)
- **.NET 8 SDK** → https://dotnet.microsoft.com/download/dotnet/8
- **VS Code** mit C# DevKit Extension
- Receiver mit **OpenWebif** (Port 80 Standard)

---

## Setup in VS Code

```bash
# 1. Ins Projektverzeichnis wechseln
cd DreamWin

# 2. Abhängigkeiten laden (libVLC wird automatisch heruntergeladen)
dotnet restore

# 3. Starten
dotnet run
```

### Build (Release)
```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
# → Ausgabe: bin/Release/net8.0-windows/win-x64/publish/DreamWin.exe
```

---

## Receiver einrichten

1. App starten → **Settings** in der Sidebar
2. **"+ Add Receiver"** klicken
3. Felder ausfüllen:
   - **Name**: z.B. „Wohnzimmer DreamBox"
   - **Host/IP**: IP des Receivers (z.B. `192.168.1.100`)
   - **Web Port**: `80` (Standard OpenWebif)
   - **Stream Port**: `8001` (Standard Enigma2 Streaming)
   - Username/Password nur wenn gesetzt
4. **"Save Receiver"** → **"Connect"**

---

## Receiver-Kompatibilität

| Receiver | Getestet |
|---|---|
| DreamBox (DM500HD, DM900, DM7080...) | ✅ |
| VU+ (Solo, Duo, Zero, Ultimo...) | ✅ |
| Gigablue | ✅ |
| Xtrend | ✅ |
| Alle mit OpenWebif-Plugin | ✅ |

**OpenWebif** muss auf dem Receiver installiert sein (in den meisten aktuellen Images bereits enthalten).

---

## Architektur

```
DreamWin/
├── Models/           # Datenmodelle (ReceiverConfig, EPG, Timer, Movie...)
├── ViewModels/       # MVVM ViewModels (CommunityToolkit.Mvvm)
│   ├── MainViewModel.cs
│   ├── LiveTVViewModel.cs
│   └── MediaViewModels.cs  (EPG, Timer, Movies)
├── Views/            # WPF UserControls + MainWindow
│   ├── MainWindow.xaml
│   ├── LiveTVView.xaml     (libVLC VideoView)
│   ├── EpgView.xaml
│   ├── TimersView.xaml
│   ├── MoviesView.xaml     (libVLC VideoView)
│   └── SettingsView.xaml
├── Services/
│   ├── Enigma2Service.cs   # OpenWebif REST API
│   └── SettingsService.cs  # Persistenz
└── Converters/       # WPF Value Converters
```

---

## Streaming-URLs

Der Receiver streamt über Port 8001 (Standard):
- **Live TV**: `http://<ip>:8001/<service_ref>`
- **Aufnahmen**: `http://<ip>:8001/file?file=<path>`

libVLC übernimmt Demux, Codec-Handling und Pufferung automatisch.

---

## Geplante Erweiterungen

- [ ] Screenshot vom Receiver anzeigen
- [ ] Fernbedienung (Remote Control Panel)
- [ ] Timer-Editor (manuelle Zeit eingeben)
- [ ] EPG Zeitstrahl (Grid-View)
- [ ] Mehrfach-Aufnahme-Ordner
- [ ] Subtitles / Tonspuren wechseln
- [ ] Programmvorschau als Kachel-Ansicht
