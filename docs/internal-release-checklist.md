# PyMonitor 내부 GitHub Release 체크리스트

이 문서는 PyMonitor 설치 파일을 GitHub Release에 게시할 때 사용하는 짧은
운영 절차입니다. 빌드와 서명 구조의 상세 설명은
[Release hardening](release.md)을 참고합니다.

## 자체 서명 인증서의 범위

- 자체 서명 PFX로 만든 Release는 내부 다운로드와 수동 설치 시험용입니다.
- 태그로 게시한 자체 서명 빌드는 자동으로 GitHub `Pre-release`가 됩니다. 공인
  인증서 빌드만 안정 Release로 게시됩니다.
- 인증서를 신뢰하지 않는 PC에서는 Windows가 `알 수 없는 게시자` 경고를
  표시합니다.
- 인앱 업데이트는 Windows가 신뢰하는 MSI 서명을 요구하므로 신뢰되지 않은 자체
  서명 MSI를 설치하지 않습니다. 자동 업데이트 성공 흐름까지 시험하려면 테스트
  PC에 인증서의 공개 부분을 신뢰하도록 설치하거나 공인 코드 서명 인증서를
  사용해야 합니다.
- PFX와 암호는 저장소, Release, Actions artifact, 로그에 넣지 않습니다.

## 1. 최초 한 번만 설정

저장소의 **Settings > Secrets and variables > Actions**에 다음 Repository
Secrets를 등록합니다.

- `WINDOWS_CERTIFICATE_BASE64`: 코드 서명 PFX 파일의 Base64 값
- `WINDOWS_CERTIFICATE_PASSWORD`: 해당 PFX의 실제 암호

이름이 등록됐는지만 확인합니다. 명령은 비밀값을 출력하지 않습니다.

```powershell
gh secret list --repo MouseBall54/PyMonitor
```

두 Secret은 임의 문자열이 아니며 반드시 같은 PFX와 암호의 조합이어야 합니다.

## 2. 태그 전 상태 확인

저장소 루트에서 실행합니다.

```powershell
git fetch origin --tags
git status --short --branch

[xml]$props = Get-Content .\Directory.Build.props
$version = [string]$props.Project.PropertyGroup.Version
$version

git rev-parse HEAD
git rev-parse origin/master
gh run list --workflow CI --branch master --limit 1
```

다음을 모두 만족해야 합니다.

- 작업 트리에 의도하지 않은 변경이 없습니다.
- `HEAD`와 `origin/master`가 같습니다.
- 최신 CI가 성공했습니다.
- 배포하려는 버전과 `Directory.Build.props`의 버전이 같습니다.
- 이미 설치된 같은 버전에서 업데이트를 시험하려면 먼저 다음 버전으로 올립니다.

버전을 변경하는 경우 관련 파일과 테스트 범위는
[Prepare every stable version](release.md#prepare-every-stable-version)을 따릅니다.

## 3. 서명 시험 빌드

실제 태그를 만들기 전에 수동 워크플로를 실행합니다.

```powershell
gh workflow run release.yml --ref master
Start-Sleep -Seconds 3

$runId = gh run list `
  --workflow release.yml `
  --event workflow_dispatch `
  --limit 1 `
  --json databaseId `
  --jq '.[0].databaseId'

gh run watch $runId --exit-status
```

성공하면 `PyMonitor-signed` Actions artifact를 내려받아 확인할 수 있습니다.

```powershell
$dryRunDir = Join-Path $PWD "artifacts\release-dry-run\$runId"
gh run download $runId --name PyMonitor-signed --dir $dryRunDir
```

`workflow_dispatch`는 Release를 만들지 않습니다. 워크플로는 자체 서명 인증서를
자동 감지하여 공개 인증서만 GitHub Runner의 현재 사용자 Root 저장소에 임시로
등록하고, `signtool verify /pa`가 끝나면 `finally`에서 인증서와 PFX를 제거합니다.
서명 검증을 끄거나 unsigned 파일을 대신 게시하지 않습니다.

## 4. Release 태그 게시

시험 빌드가 성공한 뒤 현재 제품 버전과 정확히 같은 annotated tag를 게시합니다.
아래 예시는 현재 `$version` 값을 그대로 사용합니다.

```powershell
$existingTag = git ls-remote --tags origin "refs/tags/v$version"
if ($existingTag) { throw "v$version tag already exists." }

git tag -a "v$version" -m "PyMonitor $version"
git push origin "v$version"
```

태그 푸시는 `PyMonitor Signed Release` 워크플로를 자동 실행합니다. 이 워크플로가
테스트, EXE/MSI 서명, SHA-256 생성과 검증을 마친 뒤 GitHub Release를 생성하므로
ZIP이나 MSI를 수동으로 올리지 않습니다. 자체 서명 인증서이면 내부용
`Pre-release`, 공인 인증서이면 안정 Release가 생성됩니다.

```powershell
Start-Sleep -Seconds 3
$releaseRunId = gh run list `
  --workflow release.yml `
  --limit 1 `
  --json databaseId `
  --jq '.[0].databaseId'

gh run watch $releaseRunId --exit-status
```

## 5. 게시 결과 확인

```powershell
gh release view "v$version" --web
gh release view "v$version" --json assets --jq '.assets[].name'
```

Release에는 다음 네 파일만 있어야 합니다.

```text
PyMonitor-X.Y.Z-win-x64.zip
PyMonitor-X.Y.Z-win-x64.zip.sha256
PyMonitor-X.Y.Z-win-x64.msi
PyMonitor-X.Y.Z-win-x64.msi.sha256
```

새 폴더에 네 파일을 내려받아 ZIP/MSI의 SHA-256과 sidecar가 일치하는지 확인하고,
테스트 PC에서 설치, 실행, 제거를 확인합니다. 자체 서명 Release는 `알 수 없는
게시자` 경고가 예상되므로 파일 출처와 hash를 먼저 확인합니다.

## 실패 시 원칙

- 첫 번째 실패 단계의 로그를 고친 뒤 다시 시험합니다.
- 일시적인 Runner 또는 네트워크 오류만 있었다면 같은 실행을 재시도할 수 있습니다.
- 태그 이후 소스 수정이 필요하면 기존 태그를 강제로 옮기지 말고 버전을 올려 새
  태그를 만듭니다.
- 일부 파일만 있는 불완전한 Release는 원인을 확인한 뒤 제거하고 다시 게시합니다.
- 일반 CI의 `PyMonitor-unsigned` artifact는 공개 Release 파일로 사용하지 않습니다.
