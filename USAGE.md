# Windget 사용법

Current public version: `v0.1.6`

Windget은 Windows 데스크톱 위에 배치해서 사용하는 WPF 위젯 앱입니다. 메모, 시스템 리소스, Sound Mixer, 캘린더, Timer / Stopwatch, Quick Launcher를 투명한 데스크톱 캔버스 위에 배치할 수 있습니다.

## 설치

### Release ZIP으로 설치

1. GitHub Releases에서 `Windget-v0.1.6-win-x64.zip` 파일을 다운로드합니다.
2. 원하는 위치에 압축을 풉니다.
3. 압축을 푼 폴더에서 `WindgetApp.exe`를 실행합니다.
4. Windows 보안 경고가 표시되면 신뢰하는 파일인지 확인한 뒤 실행합니다.

권장 설치 위치:

```text
C:\Users\<사용자 이름>\Apps\Windget\
```

`Start With Windows`는 현재 실행 중인 `WindgetApp.exe` 경로를 Windows 시작 프로그램에 등록합니다. 폴더를 옮겼다면 Windget을 다시 실행한 뒤 `Start With Windows`를 껐다가 다시 켜 주세요.

### 소스에서 실행

.NET SDK가 설치되어 있다면 소스에서 직접 실행할 수 있습니다.

```powershell
cd WindgetApp
dotnet build
dotnet run
```

배포용 파일 생성:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

## 기본 조작

- 위젯 상단 제목 영역을 드래그하면 위치를 옮길 수 있습니다.
- 위젯 오른쪽 아래 모서리를 드래그하면 크기를 조절할 수 있습니다.
- 위젯을 이동하거나 크기를 조절할 때 다른 위젯의 중앙, 끝, 기준선에 가까워지면 정렬 가이드가 표시됩니다.
- `Control Center`의 `Auto` 버튼은 현재 해상도에 맞춰 위젯 크기와 위치를 자동 정리합니다.
- `Save` 버튼은 위젯 위치, 크기, 투명도, 표시 여부를 저장합니다.
- 시스템 트레이의 Windget 아이콘을 왼쪽 클릭하면 다른 위젯은 그대로 두고 `Control Center`만 숨기거나 표시할 수 있습니다.
- `Hide` 액션은 창을 시스템 트레이로 숨깁니다.
- `Exit` 액션은 앱을 종료합니다.

## Control Center

- `Global Opacity`: 전체 투명도 조절
- `Always On Top`: 항상 위에 표시
- `Start With Windows`: Windows 로그인 시 자동 실행 등록 또는 해제
- 위젯 토글: 원하는 위젯 표시 또는 숨김
- `Auto`: 해상도 기반 자동 배치
- `Save`: 현재 레이아웃 저장

## Memo

- 제목과 내용을 가진 메모 카드를 관리합니다.
- 메모 제목은 카드 안에서 바로 수정할 수 있습니다.
- `Open / Done`으로 완료 상태를 바꿀 수 있습니다.
- 메모별 설정에서 자동 초기화 조건을 설정할 수 있습니다.
- `After Done`: 완료 후 지정한 `Day / Hour / Minute` 뒤 초기화
- `At Time`: 시스템 시간 기준 지정 시각에 초기화
- `Monthly`: 매월 지정한 날짜와 시간에 초기화

## System

- CPU 사용률
- Memory 사용률
- GPU 사용률
- Network 송수신 속도
- App Memory
- CPU / Memory / GPU 그래프

## Sound Mixer

Sound Mixer는 Windows 기본 출력/입력 장치와 현재 오디오 세션 볼륨을 조절합니다.

- `Playback Device`: 현재 재생 장치 확인 및 기본 재생 장치 변경
- `Recording Device`: 현재 녹음 장치 확인 및 기본 녹음 장치 변경
- 장치 목록은 Windows 오디오 엔드포인트와 MMDevices 정보를 함께 사용합니다.
- v0.1.2부터 Windows MMDevice 엔드포인트 열거가 정상 동작하도록 수정되어 기본 장치 변경이 더 안정적으로 적용됩니다.
- v0.1.3부터 선택한 장치가 활성 재생/녹음 엔드포인트인지 먼저 검증하고, 변경 성공 또는 실패 사유를 Sound Mixer에 표시합니다.
- v0.1.5부터 앱별 출력 장치 변경은 안정성을 위해 Windows `App Volume Settings` 화면에서 설정합니다.
- v0.1.6부터 시스템 오디오 세션 이름과 게임/보호된 프로세스 아이콘 인식이 개선됩니다.
- Windget은 앱별 출력 장치 설정 화면을 열어 주며, 실제 앱별 라우팅은 Windows가 관리합니다.
- 장치 선택창은 위젯 내부의 버튼 위치에 맞춰 열리고, 목록이 길면 스크롤됩니다.
- `Master Volume`: 전체 출력 볼륨 조절
- `Mute`: 전체 출력 음소거
- 앱별 오디오 세션 볼륨 조절
- 앱별 오디오 세션 음소거
- 앱별 오디오 세션 출력 설정 열기

## Calendar

- 날짜를 클릭하면 해당 날짜의 이벤트가 표시됩니다.
- 이벤트에는 제목, 위치, 시작 시간, 종료 시간을 넣을 수 있습니다.
- 시작/종료 시간은 시간 선택 UI로 고를 수 있습니다.

## Timer / Stopwatch

- `Timer`: 지정한 시간부터 카운트다운
- `Stopwatch`: 0부터 경과 시간 측정
- Timer 모드에서 알람을 켜면 시간이 끝났을 때 Windows 알림이 표시됩니다.
- 시간 선택은 휠 스크롤 방식입니다.

## Quick Launcher

- 파일, 폴더, 바로가기를 카테고리 영역으로 드래그하면 등록됩니다.
- 원하는 카테고리를 직접 만들 수 있습니다.
- 카테고리를 삭제하면 그 안의 바로가기는 삭제되지 않고 `General`로 이동합니다.
- 설정에서 `Icon Only`를 켜면 바로가기를 아이콘 중심으로 표시합니다.

## 시스템 트레이

창을 숨기면 시스템 트레이에 남습니다. 아이콘이 바로 보이지 않으면 Windows 작업 표시줄 오른쪽의 숨겨진 아이콘 영역에서 찾을 수 있습니다.

- 트레이 아이콘 왼쪽 클릭: Control Center 표시 또는 숨김
- 트레이 아이콘 더블 클릭: 위젯 다시 열기
- 트레이 메뉴 `Open Widgets`: 위젯 다시 열기
- 트레이 메뉴 `Control Center`: Control Center 표시 또는 숨김
- 트레이 메뉴 `Exit`: 종료

## AI 사용 명시

이 프로젝트는 기획, UI 설계, 코드 작성, 아이콘 제작, 문서 작성 과정에서 AI 도구의 도움을 받아 제작되었습니다. 최종 기능 구성과 수정 방향은 사용자 피드백을 바탕으로 반복 개선되었습니다.
