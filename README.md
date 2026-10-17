# modding-setup

gets BepInEx running on Scam With Your Friends so you can load mods.

## how to use

**Windows**
```powershell
git clone https://github.com/Scam-and-Mod-with-Friends/Modding-Setup.git
cd Modding-Setup
.\setup.ps1
```

**Linux / Mac**
```bash
git clone https://github.com/Scam-and-Mod-with-Friends/Modding-Setup.git
cd Modding-Setup
chmod +x setup.sh && ./setup.sh
```

it'll find your game on its own if you installed through steam. after it's done, put mod `.dll` files in `BepInEx/plugins/` in your game folder.

**Linux (Proton):** set this in Steam launch options (right-click game -> Properties -> Launch Options):
```
WINEDLLOVERRIDES="winhttp=n,b" %command%
```
then launch normally through Steam.

if the game updated and mods broke, just re-run the setup script.

## game not found?

```powershell
.\setup.ps1 -GameDir "C:\path\to\Scam With Your Friends"
```

## other flags

- `-Offline` - don't download BepInEx (if you already have it installed)
- `-BepInExZip path` - point it at a zip you downloaded yourself

---

not affiliated with or endorsed by Jaterer Worldwide.
