# PyMonitor 설치파일 빌드 가이드

이 문서는 Windows에서 PyMonitor의 Portable ZIP과 MSI 설치파일을 로컬로
빌드하는 절차를 설명합니다. 모든 명령은 **저장소 루트**에서 PowerShell로
실행합니다.

## 1. 준비 사항

다음 도구가 필요합니다.

- Windows x64
- .NET SDK 10.0.301 (`global.json`에 지정된 버전)
- CPython 3.10~3.14 standard-GIL x64
- 처음 빌드할 때 NuGet 패키지를 복원할 수 있는 네트워크 연결

설치 상태를 확인합니다.

```powershell
dotnet --version
python --version
```

`dotnet` 또는 `python`이 `PATH`에 없다면 실행 파일 경로를 환경 변수로
지정할 수 있습니다.

```powershell
$env:DOTNET_EXE = 'C:\path\to\dotnet.exe'
$env:PYTHON_EXECUTABLE = 'C:\path\to\python.exe'
```

## 2. Portable 배포본 빌드

먼저 MSI에 포함할 self-contained Portable 배포본을 만듭니다.

```powershell
.\scripts\Build-PortableRelease.ps1
```

이 스크립트는 다음 작업을 수행합니다.

1. Python Agent와 .NET 테스트 실행
2. `win-x64` self-contained 애플리케이션 게시
3. Agent, 샘플, README와 문서 포함 여부 검증
4. Portable ZIP과 SHA-256 파일 생성

산출물은 다음 위치에 생성됩니다. `<version>`은
`Directory.Build.props`의 `Version` 값입니다.

```text
artifacts\PyMonitor-<version>-win-x64\
artifacts\PyMonitor-<version>-win-x64.zip
artifacts\PyMonitor-<version>-win-x64.zip.sha256
```

## 3. MSI 설치파일 빌드

Portable 빌드가 성공한 뒤 다음 명령을 실행합니다.

```powershell
.\scripts\Build-Installer.ps1
```

WiX Toolset SDK가 x64 MSI를 만들고 MSI 메타데이터, SHA-256 및 관리 설치
추출 결과를 검증합니다. WiX SDK는 프로젝트에 패키지로 지정되어 있으므로 별도의
WiX 전역 설치는 필요하지 않습니다.

최종 설치파일은 다음 위치에 생성됩니다.

```text
artifacts\installer\PyMonitor-<version>-win-x64.msi
artifacts\installer\PyMonitor-<version>-win-x64.msi.sha256
```

일반 사용자에게 배포할 파일은 `.msi`와 무결성 확인용 `.msi.sha256`입니다.

## 4. 전체 빌드 명령 요약

```powershell
.\scripts\Build-PortableRelease.ps1
.\scripts\Build-Installer.ps1
```

개발 중 테스트를 이미 완료하여 빠르게 다시 패키징할 때만 Portable 테스트를
생략할 수 있습니다.

```powershell
.\scripts\Build-PortableRelease.ps1 -SkipTests
.\scripts\Build-Installer.ps1
```

정식 배포 파일을 만들 때는 `-SkipTests`를 사용하지 않습니다.

## 5. 코드 서명 빌드

기본 빌드는 Authenticode 서명이 없는 unsigned 빌드입니다. 신뢰할 수 있는 코드
서명 PFX가 있다면 EXE와 MSI를 서명하는 전체 빌드를 실행할 수 있습니다.

```powershell
$password = Read-Host 'PFX password' -AsSecureString
.\scripts\Build-Release.ps1 `
  -CertificatePath C:\secure\release-signing.pfx `
  -CertificatePassword $password
```

PFX 파일과 암호는 저장소에 커밋하지 않습니다. 서명 빌드에는 Windows SDK의
`signtool.exe`와 타임스탬프 서버 연결도 필요합니다.

## 6. 문제 해결

### `python`을 찾을 수 없는 경우

설치된 Python 실행 파일을 직접 지정한 뒤 다시 빌드합니다.

```powershell
$env:PYTHON_EXECUTABLE = 'C:\path\to\python.exe'
.\scripts\Build-PortableRelease.ps1
```

### Portable 배포본이 없다는 오류가 발생하는 경우

MSI는 Portable 배포 폴더를 입력으로 사용합니다. Portable 빌드를 먼저 실행합니다.

```powershell
.\scripts\Build-PortableRelease.ps1
.\scripts\Build-Installer.ps1
```

### 이전 산출물과 구분해야 하는 경우

빌드 버전은 `Directory.Build.props`에서 관리합니다. 버전을 변경한 후 두 빌드
명령을 처음부터 다시 실행합니다. 생성된 `artifacts\` 파일은 소스 관리에
커밋하지 않습니다.

더 자세한 릴리스 검증과 GitHub Release 절차는 [release.md](release.md)를
참고합니다.
