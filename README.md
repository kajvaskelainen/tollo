# Töllö – IPTV-soitin Windowsille

Töllö on Windows-soitin Xtream Codes -yhteensopiville IPTV-palveluille ja M3U-soittolistoille:
live-kanavat, TV-opas, elokuvat ja sarjat. **Töllö on pelkkä soitin – se ei sisällä eikä välitä mitään sisältöä.**
Tarvitset oman IPTV-tilauksen (palvelimen osoite, käyttäjätunnus ja salasana tai M3U-soittolista).

Ominaisuuksia: TV-opas (myös ilmaiset ulkoiset XMLTV-oppaat), jälkikatselu (catch-up), muistutukset,
tallennus ja ajastetut tallennukset, pienoissoitin, monikatselu (2×2), jatka katsomista ja sarjojen
seuraava jakso, äänen synkronointi, kuvasuhde ja zoom, äänen tasoitus, uniajastin, automaattinen
uudelleenyhdistys, Chromecast-suoratoisto, useat tilit, kanavien hallinta ja varmuuskopio.

## Asennus

1. Lataa uusin `Tollo-x.y.z-win-x64.zip` [Releases](../../releases)-sivulta.
2. Pura zip haluamaasi kansioon (esim. `C:\Ohjelmat\Tollo`) ja käynnistä `Tollo.exe`.
   Koko kansio tarvitaan – älä siirrä pelkkää exeä. Pikakuvakkeen voi tehdä työpöydälle.
3. Windows voi varoittaa tuntemattomasta julkaisijasta, koska ohjelmaa ei ole allekirjoitettu:
   valitse **Lisätietoja → Suorita silti**.

Vaatii 64-bittisen Windows 10:n tai 11:n. .NET:iä ei tarvitse asentaa.

### Tietoturva

- Salasana tallennetaan vain omalle koneellesi Windowsin DPAPI-salauksella omaan käyttäjätiliisi sidottuna.
  **Kirjaudu ulos** poistaa sen.
- Xtream Codes -rajapinta lähettää tunnukset jokaisen pyynnön osoitteessa. Jos palvelimen osoite alkaa
  `http://`, yhteys on salaamaton: käytä `https://`-osoitetta, jos palveluntarjoaja tukee sitä,
  äläkä käytä IPTV-salasanaa missään muualla.

## Kehitys

C# / .NET 8, WPF, LibVLCSharp, CommunityToolkit.Mvvm.

### Ajaminen

```bash
dotnet run --project Kanava
```

Kehityksessä (Debug-versio) voi kirjautua suoraan komentoriviltä. Julkaistussa versiossa tämä ei ole käytössä:

```bash
dotnet run --project Kanava -- --server http://localhost:8085 --user test --pass test
```

### Julkaisu (.exe)

```bash
dotnet publish Kanava/Kanava.csproj -c Release -o Ohjelma
```

Tuottaa kansioon `Ohjelma` itsenäisen version (`Tollo.exe`), joka ei vaadi .NET:n asennusta.
Koko kansio tarvitaan (mm. `libvlc`), joten pikakuvake tehdään `Tollo.exe`:hen eikä exeä siirretä yksinään.
Kirjautumistiedot, suosikit ja järjestykset ovat `%AppData%\Tollo`-kansiossa, joten ne säilyvät päivityksissä.
Jaettava zip tehdään pakkaamalla `Ohjelma`-kansion sisältö, ja se liitetään GitHubin releaseen.

## Lisenssit

Kolmannen osapuolen komponenttien lisenssit (mm. LibVLC, LGPL-2.1): [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Tiedosto on mukana myös jaettavassa zipissä.

### Testipalvelin (MockXtream)

Paikallinen Xtream-palvelin, jossa on 320 kanavaa, XMLTV-opas, 240 elokuvaa ja 60 sarjaa.
Videona se lähettää liikkuvaa MJPEG-testikuvaa. Tunnukset: `test` / `test`.

```bash
dotnet run --project MockXtream -- 8085
```

## Rakenne

| Kansio | Sisältö |
|---|---|
| `Models/` | Xtream-tietomallit (kanava, VOD, sarja, EPG-ohjelma) |
| `Services/XtreamClient.cs` | `player_api.php`-kutsut, URL:t (`/live`, `/movie`, `/series`, `xmltv.php`) |
| `Services/EpgService.cs` | XMLTV: lataus taustalla, levyvälimuisti, päivitys kerran vuorokaudessa, suoratoistojäsennys |
| `Services/EpgMatcher.cs` | Ulkoisten XMLTV-oppaiden kanavien yhdistäminen nimen perusteella (FI:, HD, .fi yms. ohitetaan) |
| `Services/SettingsStore.cs` | Asetukset `%AppData%\Tollo`, salasana DPAPI:lla (CurrentUser) |
| `Services/UserDataStore.cs` | Suosikit ja historia profiilikohtaisesti |
| `Services/PlayerService.cs` | Yhteinen LibVLC, VLC-kutsut järjestyksessä taustasäikeessä |
| `Services/ImageCache.cs` | Logojen/julisteiden asynkroninen lataus ja välimuisti |
| `Controls/EpgGrid.cs` | Itse piirtävä, virtualisoitu TV-opasruudukko (synkronoitu vieritys) |
| `Controls/CornerMask.cs` | Pyöristetyt kulmat videolle VideoView:n overlayssa (airspace) |
| `Views/PipWindow.xaml` | Pienoissoitin: aina päällimmäinen kulmaikkuna omalla soittimellaan |
| `ViewModels/`, `Views/` | MVVM-näkymät: kirjautuminen, Live TV, elokuvat, sarjat, haku, suosikit, asetukset |

## Pikanäppäimet

- Live TV: klikkaus = esikatselu, tuplaklikkaus / F / F11 = koko näyttö, Esc = takaisin, P = pienoissoitin
- Kanavanvaihto: numerot = kanavanumero, Backspace = edellinen kanava, PageUp/PageDown = kanava ylös/alas
- Ääni: M = mykistys, J/K = ääni 0,1 s aiemmin/myöhemmin (muistetaan kanavakohtaisesti)
- Pienoissoitin: raahaa = siirrä kulmaan, rulla = äänenvoimakkuus, tuplaklikkaus = takaisin Töllöön
- TV-opas: rulla = kanavat, Shift+rulla tai vetäminen = aika, ↑↓ = kanava, Enter = koko näyttö,
  hiiren oikea = jälkikatselu / muistutus / tallennus, tuplaklikkaus menneeseen ohjelmaan = jälkikatselu
- Elokuvat/jaksot: välilyönti = tauko, ← −10 s, → +30 s, ↑↓ = äänenvoimakkuus, Home = alkuun, N = seuraava jakso, Esc = sulje
- Monikatselu: kaksoisklikkaa kanavaa, klikkaa ruutua (ääni), 1–4 = valitse ruutu, Delete = tyhjennä, F11 = koko näyttö

## Tiedostot

- `%AppData%\Tollo\settings.json` – asetukset ja salattu salasana
- `%AppData%\Tollo\userdata_*.json` – suosikit ja historia
- `%LocalAppData%\Tollo\cache\epg_*.xml` – ohjelmaopas
- `%LocalAppData%\Tollo\cache\error.log` – käsittelemättömät virheet
