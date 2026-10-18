# Mouse Without Borders – opcionális UAC- és bejelentkezéskezelés

Helyi **1.2.0-preview.3 tesztváltozat**, a Portable fork MIT-licencű forrásából. PowerToys
nem szükséges. A ZIP önmagában nem telepít és nem indít rendszerszolgáltatást.

## Először a normál működést állítsd be

1. Csomagold ki a ZIP-et egy saját mappába.
2. A régi Mouse Without Bordersből lépj ki, mielőtt az újat próbálod, hogy ne
   foglalják el egymás hálózati portját. A telepítő ezt nem végzi el helyetted.
3. A `portable\MouseWithoutBorders.exe` ugyanebből a tesztváltozatból fusson
   mindkét gépen. Állítsd be a gépek nevét/elrendezését és ugyanazt a kulcsot.
4. Először normál asztalon ellenőrizd az egeret, billentyűzetet és vágólapot.

## Teljes képernyős alkalmazások és játékok

A preview.3 új kapcsolója: **Settings → Other Options → Block edge switching in
full-screen apps**. Új beállításoknál be van kapcsolva; a korábban mentett értéket
megőrzi. Teljes képernyős vagy keret nélküli játékból történő széli gépváltáshoz
a játékot futtató gépen vedd ki a pipát. A **Switch computers at screen edge**
beállításnak is bekapcsolva kell maradnia.

Egyes játékok továbbra is magukhoz kötik az egeret. A **Keyboard Shortcuts**
résznél engedélyezd a gyorsbillentyűket, majd a **Switch PCs, Ctrl+Alt:** sorban
válaszd az **F1, F2, F3, F4** lehetőséget. Ezután a **Ctrl+Alt+F1–F4** vált a
megfelelő gépre. A felhasználó megerősítette, hogy ez Warframe mellett, a másik
gépen használt keret nélküli módban működött.

## Az UAC-kezelés feltétele

Az UAC maradjon bekapcsolva, és az emelést kérő ablak a Windows védett asztalán
jelenjen meg. A **User Account Control: Switch to the secure desktop when
prompting for elevation** Windows-házirend legyen **Engedélyezve**. Ennek
rendszerleíró beállítása a `PromptOnSecureDesktop` DWORD érték **1** a következő
kulcsban:
`HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`.

Ha `PromptOnSecureDesktop=0`, az UAC-ablak a normál `Default` asztalon marad.
Ebben a tesztváltozatban a privilegizált ügynök kizárólag a `Winlogon` asztalon
fut; a normál asztalon megjelenő emelt jogosultságú ablakokat és alkalmazásokat
nem kezeli. Ilyenkor az egérmutató mozoghat, miközben a Windows a kattintást
jogosultsági okból blokkolja.

Sem a telepítő, sem a szolgáltatás nem módosítja automatikusan ezt az
UAC-házirendet. A szükséges módosítás külön rendszergazdai művelet.

## Külön bekapcsolható szolgáltatás

A védett Windows-képernyőn futó ügynök SYSTEM-jogot igényel. Csak egér- és
billentyűzetbevitelt fogad; vágólapot, fájlátvitelt és képernyőképet ott nem kezel.
A normál program továbbra is a saját felhasználói jogosultságoddal fut.

A csomag mappájában nyiss normál PowerShellt, és exportáld az adott gép profilját:

```powershell
& .\portable\MouseWithoutBorders.exe --export-secure-desktop-profile "$PWD\secure-profile.json"
```

A fájl a megosztott kulcsot is tartalmazza: helyben maradjon. Hosszú, véletlen
kulcsot használj. Ezután – a szkriptek átnézése és jóváhagyás után –
**rendszergazdaként megnyitott, 64 bites PowerShell 7-ben**:

```powershell
.\install-secure-service.ps1 -ProfilePath .\secure-profile.json -StartService
```

Ez az új `MouseWithoutBorders.SecureDesktop` szolgáltatást telepíti, védett
Program Files / ProgramData mappákat készít, és kizárólag a helyi alhálózat
privát hálózati profilján engedélyezi az ügynök beviteli portját. A régi Garage
vagy PowerToys szolgáltatását nem módosítja. Más géphez külön, azon a gépen
exportált profil kell.

Kulcs- vagy elrendezésváltoztatás után új export és rendszergazdai telepítés kell.
Előzetes ellenőrzés rendszermódosítás nélkül:

```powershell
.\install-secure-service.ps1 -ProfilePath .\secure-profile.json -ValidateOnly
```

## Tesztállapot és korlátok

A preview.2 védett UAC-kezelése két valódi gép között sikeresen működött. A preview.3
az UAC bezárása utáni visszakapcsolódást javítja: az új kétgépes próba sikerült,
és a felhasználó érezhetően rövidebb megakadást jelzett.
A zárolás/feloldás, a bejelentkezés és az alvás/ébredés még nincs ellenőrizve.
A fordítási és automatikus eredmények, valamint az örökölt tesztcsomag ismert
megjelenítési és területi beállítástól függő hibái:
`SECURE_DESKTOP_VALIDATION.md`.

A Ctrl+Alt+Del/SAS, az RDP és a több aktív munkamenet nem része ennek a
tesztváltozatnak. A korábbi Modern Standby alatti lecsatlakozás javítását ez a
szolgáltatás önmagában nem garantálja.

## Visszavonás

Rendszergazdai PowerShellből:

```powershell
.\remove-secure-service.ps1
```

Csak az új szolgáltatás regisztrációja és saját tűzfalszabálya törlődik. A védett
fájlok/profil ellenőrzéshez megmaradnak; a normál program és beállításai maradnak.
