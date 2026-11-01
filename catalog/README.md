# Gpixel 온라인 에셋 카탈로그 원본

에셋 관리자의 **둘러보기** 탭이 읽는 온라인 목록의 원본입니다. 여기의 패키지를 검사해 홈페이지(GitHub Pages, `docs/`)의 `docs/catalog/`에 정적 index와 파일을 만듭니다. 앱은 `https://1000pro-1997.github.io/GPixel/catalog/index.json`을 HTTPS로 읽습니다.

## 구조

```text
catalog/
  <패키지 ID>/
    <패키지 ID>-<버전>.gpixel-asset   제출한 패키지 하나(바이트 그대로 게시)
    review.json                       검토 정보
```

`review.json`:

```json
{
  "schemaVersion": 1,
  "summary": "목록에 보일 짧은 설명(200자 이하)",
  "tags": ["gradient", "starter"],
  "review": "reviewed",
  "published": "2026-10-28",
  "updated": "2026-10-28",
  "preview": "previews/pack.png"
}
```

- `review`: `official`(작성자 ID `gpixel`의 패키지만) 또는 `reviewed`(검토를 마친 기여 패키지).
- `tags`: 소문자·숫자·하이픈, 8개까지.
- `preview`(선택): 패키지 안 미리보기 PNG. 없으면 첫 에셋의 미리보기를 씁니다.

## 패키지 나누기

- 에셋 하나에 패키지 하나가 기본입니다. 그래야 둘러보기에서 에셋마다 이름으로 검색되고, 미리보기가 보이고, 따로 설치·삭제·업데이트됩니다.
- 레시피는 앱의 기본 효과를 `type`으로 부르고 그라데이션·스크립트 원문은 값으로 복사하므로, 다른 패키지에 기대는 에셋은 없습니다. 공통 기능이 필요하면 앱의 기본 효과나 스크립트 기본 함수로 넣습니다(라이브러리 패키지를 두지 않음).
- 같이 써야 맞는 테마 세트(한 팔레트에 맞춘 그라데이션 묶음, 한 게임 스타일의 피격·사망·회복 효과 묶음)만 한 패키지에 묶습니다.
- 공식 패키지 ID는 `gpixel.gradients.<이름>`, `gpixel.effects.<이름>`입니다.

## 검사 규칙

- 패키지는 앱 설치와 같은 엄격 검사를 통과해야 합니다(경로, 크기, 해시, 모든 에셋 schema, 미리보기).
- 폴더 이름 = 패키지 ID, 패키지 ID의 첫 조각 = 작성자 ID. 작성자 ID `gpixel`은 공식 패키지(`official`)만 씁니다.
- 개인 내보내기 패키지(작성자 `personal`, 라이선스 `LicenseRef-Personal`)는 받지 않습니다. 패키지 안에 `LICENSE.txt`나 `LICENSE.md`가 있어야 합니다.
- 목록 미리보기가 있어야 합니다(`review.json`의 `preview`나 미리보기가 있는 에셋).
- 다른 제출과 이름이나 패키지 파일이 겹치면 안 됩니다.
- 이미 게시한 버전의 내용은 바꿀 수 없고 버전을 내릴 수 없습니다. 고친 패키지는 버전을 올려 파일을 바꿉니다(목록에는 최신 버전 하나만 남습니다).

이 규칙은 Core의 `AssetPublishCheck`에 한 벌만 있고, `tools/CatalogBuilder`(CI)와 앱의 **온라인에 올리기** 창이 같이 씁니다. 앱은 게시 버전·이름 겹침을 마지막으로 받은 온라인 목록과 비교하므로, 그 뒤에 게시된 패키지와 겹치는 경우만 CI에서 새로 걸릴 수 있습니다.

## 제출하기 (기여자)

1. Gpixel의 **에셋 관리자**에서 개인 에셋을 고르고 **온라인에 올리기…** 를 누릅니다.
2. 작성자 ID·표시 이름(다음에도 기억), 패키지 ID(`작성자 ID.이름`)·버전·이름·짧은 설명·태그, 라이선스(흔한 SPDX ID나 `LicenseRef-…` + 라이선스 글), 목록 미리보기, 포함할 에셋을 정합니다. 같은 ID를 전에 게시했으면 다음 버전을 제안합니다.
3. **제출 전 검사**에 오류가 없으면 **저장하고 제출 페이지 열기…** 로 `<ID>-<버전>.gpixel-asset`과 같은 폴더의 `<ID>-<버전>-submission.zip`(이 폴더 구조 그대로 `<ID>/` 아래 패키지와 `review.json`)을 저장합니다. 브라우저에 [에셋 제출 양식](https://github.com/1000Pro-1997/GPixel/issues/new?template=asset-submission.yml)이 패키지 정보로 채워져 열립니다.
4. 양식의 첨부 칸에 submission zip을 끌어다 놓고 제출합니다(GitHub는 `.gpixel-asset` 첨부를 받지 않습니다). 앱은 아무것도 올리지 않습니다.

## 제출 처리 (관리자)

1. 이슈의 zip을 받아 저장소의 `catalog/`에 풉니다(`catalog/<ID>/<ID>-<버전>.gpixel-asset`, `catalog/<ID>/review.json`). 새 버전이면 그 폴더의 예전 패키지 파일을 지우고, `review.json`의 `published`는 처음 게시한 날짜로 둡니다(앱은 받은 목록에 있으면 그 날짜를 씁니다).
2. 에셋과 미리보기를 직접 보고 검토합니다. `review`는 `reviewed`(앱이 쓰는 값) 그대로 둡니다.
3. `dotnet run --project tools/CatalogBuilder`로 `docs/catalog/`를 다시 만들고 `catalog/`와 함께 커밋합니다. 규칙에 걸리면 까닭이 한 줄씩 나옵니다 — 이슈에 그대로 답하면 됩니다.
4. 양식이 붙이는 `asset-submission` 라벨은 저장소에 만들어 두어야 붙습니다.

## 만들기와 확인

```
dotnet run --project tools/CatalogBuilder            # docs/catalog/ 다시 만들기
dotnet run --project tools/CatalogBuilder -- --check # 검사만, docs/catalog/가 최신이 아니면 실패(CI)
```

`docs/catalog/`의 `index.json`(다음 쪽은 `index-2.json` …, 쪽마다 50개), `packages/`, `previews/`는 손으로 고치지 않습니다.
