# Gpixel은 Chickxel이 되었습니다

Gpixel은 이름이 **Chickxel**로 바뀌었습니다. 새 저장소: https://github.com/1000Pro-1997/Chickxel · 홈페이지: https://chickxel.com/

이 저장소는 예전 Gpixel 앱의 자동 업데이트를 Chickxel로 이어 주는 다리입니다. 예전 Gpixel은 업데이트를 이 주소(`1000Pro-1997/GPixel`)에서 확인하므로, 여기 릴리스에는 Chickxel을 예전 파일 이름(`Gpixel-windows-x64.zip`, `Gpixel-macos-*.zip`)으로 다시 묶어 올립니다. Gpixel에서 **도움말 > 업데이트 확인**을 누르면 Chickxel로 바뀌고, 설정·에셋·기록은 Chickxel이 처음 켜질 때 그대로 옮깁니다. 그 뒤 업데이트는 Chickxel 저장소에서 받습니다.

버그 제보와 의견은 [Chickxel 이슈](https://github.com/1000Pro-1997/Chickxel/issues)나 Discord로 보내 주세요.

---

# Gpixel is now Chickxel

Gpixel has been renamed to **Chickxel**. New repository: https://github.com/1000Pro-1997/Chickxel · Website: https://chickxel.com/en/

This repository bridges the auto-update of old Gpixel builds to Chickxel. Old Gpixel checks for updates here (`1000Pro-1997/GPixel`), so the release here repackages Chickxel under the old file names. Use **Help > Check for Updates** in Gpixel to switch to Chickxel; your settings, assets and history move over on first launch, and later updates come from the Chickxel repository.

Please report bugs and ideas on [Chickxel issues](https://github.com/1000Pro-1997/Chickxel/issues) or Discord.

## 다리 릴리스 만들기 / Making a bridge release

Actions의 `bridge` 워크플로를 실행합니다(입력: Chickxel 릴리스 태그). Chickxel 릴리스 파일을 받아 체크섬을 확인하고, Windows exe를 `Gpixel.exe`로, macOS `.app`을 `Gpixel.app`으로 다시 묶어 서명한 뒤 같은 태그로 이 저장소에 릴리스를 만듭니다.
