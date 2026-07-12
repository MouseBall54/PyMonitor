namespace PyRuntimeInspector.App.Help;

public sealed record HelpTopic(
    string Id,
    string Category,
    string Title,
    string Summary,
    string Keywords,
    string Overview,
    IReadOnlyList<string> Steps,
    string Example,
    IReadOnlyList<string> Notes)
{
    public string Breadcrumb => $"PyMonitor 도움말 / {Category} / {Title}";
    public bool HasSteps => Steps.Count > 0;
    public bool HasExample => !string.IsNullOrWhiteSpace(Example);
    public bool HasNotes => Notes.Count > 0;

    internal string SearchDocument => string.Join(
        '\n',
        Id,
        Category,
        Title,
        Summary,
        Keywords,
        Overview,
        string.Join('\n', Steps),
        Example,
        string.Join('\n', Notes));
}

public static class HelpCatalog
{
    private static readonly IReadOnlyList<HelpTopic> BuiltInTopics = Array.AsReadOnly<HelpTopic>(
    [
        new(
            "getting-started",
            "시작하기",
            "처음 연결하기",
            "검사할 Python이 이미 실행 중인지에 따라 Quick Attach 또는 Managed Launch를 선택합니다.",
            "시작 getting started 처음 연결 workflow quick attach managed launch connected",
            "PyMonitor는 대상 Python 프로세스 안의 Agent와 연결된 뒤 변수와 객체를 읽습니다. Process 목록에 python.exe가 보이는 것만으로는 아직 연결된 상태가 아닙니다. 이미 실행 중인 Python을 살펴볼 때는 Quick Attach, 새 스크립트를 PyMonitor에서 시작할 때는 Managed Launch가 가장 빠릅니다.",
            [
                "PyMonitor를 실행하고 상단 상태가 아직 연결되지 않음인지 확인합니다.",
                "이미 실행 중인 Python은 Process에서 정확한 PID를 고른 뒤 Quick Attach를 누릅니다.",
                "새 스크립트는 Launch 탭에서 Python과 .py 파일을 선택한 뒤 Launch를 누릅니다.",
                "Connected가 표시되면 Inspect의 Runtime Tree에서 frame, module 또는 GC source를 선택합니다.",
                "Variables에서 변수를 선택하면 오른쪽의 Overview, Object Tree, Class, 데이터 전용 보기가 같은 선택을 자세히 보여줍니다.",
            ],
            """
            선택 가이드
            - 실행 중인 cmd/VS Code Python → Quick Attach
            - 새 .py 파일을 실행하며 관찰 → Managed Launch
            - 코드가 직접 Agent를 시작함 → Cooperative Attach
            """,
            [
                "PyMonitor는 로컬 CPython 한 프로세스를 읽기 전용으로 검사합니다.",
                "연결을 해제해도 사용자가 직접 실행한 대상 Python은 계속 동작합니다.",
            ]),
        new(
            "installation",
            "설치",
            "설치, 업데이트 및 제거",
            "MSI와 포터블 ZIP 설치 방법, 필요한 프로그램, SHA-256 확인 방법을 설명합니다.",
            "설치 install MSI portable ZIP 압축 update upgrade uninstall 제거 UAC 관리자 .NET runtime Python package SHA-256",
            "PyMonitor MSI는 Windows 10/11 x64에 시스템 범위로 설치되므로 UAC 승인이 필요합니다. 포터블 ZIP은 원하는 폴더에 모두 압축 해제해 사용할 수 있습니다. 앱은 .NET Runtime과 Python Agent를 포함하지만, 검사할 CPython과 사용자 코드가 import하는 NumPy, pandas, Matplotlib, OpenCV 등의 패키지는 대상 PC의 Python 환경에 있어야 합니다.",
            [
                "MSI: PyMonitor-26.7.11-win-x64.msi를 실행하고 Windows 관리자 승인을 완료합니다.",
                "설치 후 시작 메뉴에서 PyMonitor를 실행합니다. 기본 설치 위치는 C:\\Program Files\\PyMonitor입니다.",
                "포터블: PyMonitor-26.7.11-win-x64.zip 전체를 새 폴더에 압축 해제하고 PyMonitor.exe를 실행합니다.",
                "업데이트: 새 MSI를 실행하면 설치된 이전 PyMonitor를 업그레이드합니다.",
                "제거: 설정 > 앱 > 설치된 앱 > PyMonitor > 제거를 사용합니다. 포터블은 앱 종료 후 압축 해제 폴더를 삭제합니다.",
            ],
            """
            # PowerShell에서 배포 파일 해시 확인
            Get-FileHash .\PyMonitor-26.7.11-win-x64.msi -Algorithm SHA256
            Get-Content .\PyMonitor-26.7.11-win-x64.msi.sha256
            """,
            [
                "설치 과정에서 별도 파일을 인터넷에서 내려받지 않습니다.",
                "포터블 폴더의 agent, samples, docs 파일을 PyMonitor.exe와 함께 유지해야 합니다.",
                "NumPy/OpenCV, pandas, Matplotlib 보기는 해당 라이브러리가 대상 Python에 이미 로드된 경우에만 활성화됩니다.",
            ]),
        new(
            "quick-attach",
            "연결",
            "Quick Attach와 cmd Python REPL",
            "실행 중인 Python PID를 선택하고 CPython 버전에 맞는 방식으로 연결합니다.",
            "연결 attach quick attach cmd command prompt REPL PID bootstrap paste 붙여넣기 __main__ 3.10 3.11 3.12 3.13 3.14 live safe point",
            "Quick Attach는 실행 중인 로컬 CPython을 검사하는 기본 경로입니다. CPython 3.14 이상은 공식 remote execution 경로를 사용합니다. CPython 3.10~3.13에서는 PyMonitor가 인증된 listener를 열고 완성된 bootstrap 한 줄을 클립보드에 복사하므로, 대상 Python 프롬프트에 한 번 실행해야 합니다.",
            [
                "cmd.exe에서 python을 실행하고 변수를 선언한 뒤 그 python.exe의 PID를 확인합니다.",
                "PyMonitor에서 Rescan을 누르고 정확한 PID를 선택한 뒤 Quick Attach를 누릅니다.",
                "Python 3.10~3.13은 복사된 bootstrap을 C:\\>가 아니라 Python의 >>> 프롬프트에 붙여넣고 Enter를 누릅니다.",
                "Python 3.14 이상에서 REPL이 입력 대기 중이면 remote 요청이 safe point에 도달하도록 빈 Enter를 한 번 누릅니다.",
                "Connected 후 Inspect > Modules > __main__ 또는 자동 선택된 __main__ Variables에서 선언한 변수를 확인합니다.",
                "값을 바꾸고 F5를 누르면 이전 snapshot과 달라진 행이 강조됩니다.",
            ],
            """
            C:\> python
            >>> example_value = 1235
            >>> example_items = {"name": "demo", "values": [1, 2, 3]}

            # 연결 후 값을 변경해 비교
            >>> example_value = 2468
            """,
            [
                "Process 목록 표시와 Agent 연결 완료는 서로 다른 상태입니다.",
                "3.10~3.13의 bootstrap은 통합 터미널의 C:\\>가 아니라 Python REPL 또는 VS Code Debug Console에서 실행합니다.",
                "STALE_AGENT 또는 INCOMPATIBLE_AGENT가 나오면 대상 Python을 완전히 종료하고 다시 시작합니다.",
            ]),
        new(
            "cooperative-attach",
            "연결",
            "Cooperative Attach로 연결하기",
            "Port와 Token 환경을 대상 shell에 전달하고 사용자 코드에서 Agent를 직접 시작합니다.",
            "cooperative attach 협력형 Start listener Copy environment Auto refresh Advanced connection Port Token PY_INSPECTOR_HOST PY_INSPECTOR_PORT PY_INSPECTOR_TOKEN PYTHONPATH start_inspector waiting target",
            "Cooperative Attach는 대상 코드를 수정할 수 있을 때 사용하는 가장 명시적인 연결 방식입니다. PyMonitor가 인증 listener를 먼저 열고, 대상 Python이 start_inspector()를 호출해 127.0.0.1로 역방향 연결합니다. 설치본에 포함된 Agent를 사용하므로 별도의 pip 설치는 필요하지 않습니다.",
            [
                "상단의 Advanced connection and preferences를 펼칩니다.",
                "Port와 Token을 확인하고 Start listener를 눌러 Waiting for cooperative target 상태로 만듭니다.",
                "Copy environment를 눌러 PY_INSPECTOR_HOST, PORT, TOKEN과 PYTHONPATH 설정을 복사합니다.",
                "대상 Python을 실행할 별도 PowerShell에 복사한 환경 설정을 붙여넣습니다.",
                "사용자 코드에서 pyruntime_inspector_agent의 start_inspector()를 호출한 뒤 스크립트를 실행합니다.",
                "Connected가 표시되면 Inspect에서 변수를 탐색하고, 필요하면 Auto refresh와 Interval을 조정합니다.",
            ],
            """
            from pyruntime_inspector_agent import start_inspector

            start_inspector()
            example_value = 1235
            input("PyMonitor에서 확인한 뒤 Enter를 눌러 종료하세요...")
            """,
            [
                "Start listener를 먼저 실행해야 대상 Agent가 연결할 port가 열립니다.",
                "Token은 세션 인증 정보이므로 로그나 다른 사람에게 공유하지 마십시오.",
                "Detach는 Agent 연결만 종료하며 사용자가 시작한 대상 Python은 계속 실행됩니다.",
            ]),
        new(
            "vscode-debugging",
            "연결",
            "VS Code breakpoint에서 검사하기",
            "debugpy로 멈춘 Python에 연결해 DataFrame과 OpenCV 이미지가 만들어지는 과정을 한 줄씩 확인합니다.",
            "VS Code vscode debug debugger debugpy breakpoint 중단점 Debug Console test_python_code.py pandas OpenCV cv2",
            "VS Code 디버깅 중에는 선택한 debuggee PID에 Quick Attach한 뒤 Debug Console에서 bootstrap을 실행합니다. 중단점에 멈춘 frame의 locals와 globals를 선택하고 한 줄씩 진행하면 변수 추가, 재바인딩, 배열 내부 변경을 snapshot 단위로 비교할 수 있습니다.",
            [
                "필요하면 대상 환경에 numpy, pandas, matplotlib, opencv-python을 설치합니다.",
                "samples\\test_python_code.py를 VS Code의 Python Debugger로 실행하고 관심 줄에 breakpoint를 둡니다.",
                "PyMonitor에서 debuggee의 정확한 PID를 선택하고 Quick Attach를 누릅니다.",
                "Python 3.10~3.13이면 복사된 bootstrap을 VS Code Debug Console에 붙여넣고 실행합니다.",
                "Runtime Tree에서 멈춘 frame의 Locals 또는 Globals를 선택합니다.",
                "한 줄씩 진행하며 df_sales, bgr_gradient, cv_image_color 같은 변수를 선택하고 F5로 snapshot을 갱신합니다.",
            ],
            """
            python -m pip install numpy pandas matplotlib opencv-python

            # 실습 파일
            samples\test_python_code.py
            """,
            [
                "samples\\test_python_code.py는 breakpoint 실습용이며 일반 실행하면 완료 후 종료됩니다.",
                "디버거가 Python 실행을 멈춰도 Inspector Agent의 전용 thread는 요청을 처리할 수 있도록 설계되어 있습니다.",
            ]),
        new(
            "managed-launch",
            "실행",
            "Managed Launch로 스크립트 실행하기",
            "Python interpreter, script, 인자, 작업 폴더와 환경 변수를 지정해 새 대상 프로세스를 시작합니다.",
            "managed launch 실행 script interpreter python venv conda arguments argv cwd working directory environment stdout stderr stop restart",
            "Managed Launch는 사용자 스크립트를 수정하지 않고 PyMonitor가 Agent와 함께 실행하는 권장 방식입니다. 선택한 Python executable을 그대로 사용하며 argv, cwd, 환경 변수, stdout, stderr와 exit code를 보존합니다.",
            [
                "Launch 탭을 열고 Python에서 시스템 Python, venv 또는 Conda 환경의 python.exe를 선택합니다.",
                "Script에서 실행할 .py 파일을 선택합니다.",
                "필요하면 Arguments, Working directory와 Environment overrides를 입력합니다.",
                "Launch를 누르고 Connected가 될 때까지 기다립니다.",
                "Inspect에서 Runtime Tree와 Variables를 탐색하고 PROCESS OUTPUT에서 stdout/stderr를 확인합니다.",
                "Detach는 연결만 끊고, Stop은 managed process tree를 종료하며, Restart는 같은 설정으로 다시 실행합니다.",
            ],
            """
            Python: C:\work\project\.venv\Scripts\python.exe
            Script: C:\work\project\demo.py
            Arguments: --input "C:\images\sample image.png" --threshold 0.5

            # 함께 배포되는 종합 실습
            samples\target_ux_demo.py
            """,
            [
                "창을 닫으면 orphan process 방지를 위해 실행 중인 managed target도 종료합니다.",
                "직접 Detach한 경우에는 대상 프로세스가 계속 실행됩니다.",
            ]),
        new(
            "inspect-variables",
            "검사",
            "Runtime Tree, Variables와 변경 강조",
            "frame/module/GC source를 고르고 변수 검색·필터와 이전 snapshot 비교를 사용합니다.",
            "inspect runtime tree variables 변수 locals globals builtins module __main__ scope search filter refresh F5 changed added removed rebound updated highlight 강조 12초",
            "Inspect는 Runtime Tree → Variables → 선택 상세 보기 순서의 master-detail 화면입니다. frame의 Locals/Globals, 이미 로드된 module namespace 또는 GC-tracked object 검색 결과가 변수 목록의 source가 됩니다. 자동 새로 고침은 행과 선택 위치를 유지하면서 값만 갱신합니다.",
            [
                "Runtime Tree에서 thread/frame의 Locals·Globals 또는 Modules의 namespace를 선택합니다.",
                "Variables 검색 상자에 이름, type 또는 preview 일부를 입력하고 scope/change/type 필터를 조합합니다.",
                "변수 행을 선택해 오른쪽의 상세 탭을 엽니다.",
                "F5 또는 Refresh로 즉시 snapshot을 갱신합니다. Ctrl+F는 Variables 검색란으로 이동합니다.",
                "Added, Removed, Rebound, Updated 표시와 색상으로 직전 snapshot과의 차이를 확인합니다.",
            ],
            """
            counter = 1       # 첫 snapshot
            counter = 2       # Rebound
            items = [1, 2]
            items.append(3)   # 지원 타입은 Updated로 감지 가능
            """,
            [
                "변경 강조는 UI 반영 시간을 포함해 최소 10초 이상 보이도록 기본 12초 유지됩니다.",
                "이 기능은 debugger watchpoint가 아니라 bounded snapshot 비교입니다.",
                "대형 mutable 객체의 모든 내부 변경을 매번 전체 checksum으로 검사하지는 않습니다.",
            ]),
        new(
            "objects-classes",
            "검사",
            "Object Tree와 Class & Methods",
            "선택한 객체의 계층, 현재 위치, class, MRO, method와 parameter를 안전하게 탐색합니다.",
            "object tree 객체 계층 breadcrumb 경로 parent back forward history depth cycle pin class methods method parameter signature MRO property instance field search 검색 declaring class source annotation default",
            "Variables에서 객체를 선택하면 Overview와 Object Tree가 같은 선택 컨텍스트를 사용합니다. Object Tree는 collection item, mapping value와 instance field를 그룹화하고 필요한 child만 지연 로딩합니다. Class & Methods는 선택한 instance의 class, base class, MRO, method 종류와 안전한 signature를 정리하며, 이미 로드된 class detail 트리를 재귀적으로 검색합니다.",
            [
                "Variables에서 expandable 객체를 선택합니다.",
                "Overview에서 기본 정보와 immediate child를 확인하거나 Object Tree에서 항목을 펼칩니다.",
                "상단 breadcrumb로 현재 객체 경로와 깊이를 확인하고 Back, Forward, Parent 또는 breadcrumb 항목으로 이동합니다.",
                "Load more가 나타나면 다음 100개 child를 요청합니다. cycle 표시는 조상 객체를 다시 만난 상태입니다.",
                "Class & Methods에서 Instance fields, Methods, Class attributes를 펼치고 method 종류와 parameter signature를 확인합니다.",
                "Class 검색에 이름, 종류, 선언 class, signature/detail, source 또는 parameter 내용을 입력합니다. 공백으로 나눈 모든 단어가 하나의 항목에 포함되어야 일치합니다.",
                "하위 항목이 일치하면 그 위치를 보여 주는 조상 경로가 자동으로 펼쳐집니다. Clear를 누르면 검색 전 펼침 상태로 돌아갑니다.",
                "자주 보는 객체는 Pin으로 고정해 다른 source를 탐색한 뒤 다시 엽니다.",
            ],
            """
            class Detector:
                def __init__(self, threshold: float = 0.5):
                    self.threshold = threshold

                @property
                def dangerous_property(self):
                    raise RuntimeError("Inspector must not execute this")

                def predict(self, image, limit=10):
                    return []
            """,
            [
                "Safe Mode는 property getter, arbitrary repr/getattr/dir와 callable을 실행하지 않습니다.",
                "Class 검색은 현재 선택에서 안전 한도 안에 이미 로드된 항목만 찾으며, 검색 때문에 Python 대상에 추가 요청을 보내지는 않습니다.",
                "F5 detail refresh는 검색어와 검색 전 펼침 상태를 유지하지만 새 객체 선택, Back, Forward와 Parent 이동은 검색을 초기화합니다.",
                "순환 참조, 최대 깊이, pagination과 handle 만료 상태를 명시적으로 표시합니다.",
            ]),
        new(
            "arrays-images",
            "데이터 보기",
            "NumPy와 OpenCV 이미지 보기",
            "ndarray의 shape·dtype·strides와 이미지 preview, pixel, histogram, layout을 확인합니다.",
            "numpy ndarray array 배열 opencv cv2 image 이미지 BGR RGB BGRA HWC CHW grayscale volume slice pixel histogram dtype shape strides nbytes loading",
            "NumPy가 대상 프로세스에 이미 로드되어 있고 선택한 값이 exact ndarray일 때 Array and Image 탭이 활성화됩니다. OpenCV 이미지는 NumPy ndarray이므로 같은 탭에서 BGR/RGB layout을 지정해 봅니다. 먼저 bounded preview를 받고 확대 영역과 pixel 값은 필요한 좌표만 요청합니다.",
            [
                "Variables에서 ndarray 변수를 선택해 Array and Image 탭이 자동으로 열리는지 확인합니다.",
                "shape, dtype, strides, payload, object/data address와 owner 관계를 확인합니다.",
                "Color/layout에서 GRAY, RGB, BGR, RGBA, BGRA, HWC, CHW 또는 volume slice를 선택합니다.",
                "Fit/1:1, 휠 zoom과 우클릭 drag pan을 사용합니다.",
                "이미지를 가리키거나 클릭해 원본 좌표와 값을 확인하고 Histogram 또는 source tile을 요청합니다.",
                "배열이 바뀌면 F5 또는 자동 새로 고침으로 같은 layout/slice의 새 preview를 받습니다.",
            ],
            """
            import numpy as np

            height, width = 240, 320
            bgr_gradient = np.zeros((height, width, 3), dtype=np.uint8)
            for y in range(height):
                for x in range(width):
                    bgr_gradient[y, x] = [x % 256, y % 256, (x + y) % 256]

            cv_image_color = bgr_gradient.copy()
            """,
            [
                "preview와 tile은 최대 1024×1024이며 전체 원본 배열을 전송하지 않습니다.",
                "레이아웃이 모호하면 자동으로 단정하지 않고 사용자가 override해야 합니다.",
                "대형 배열 변경 감지는 bounded sample을 사용하므로 모든 byte의 실시간 감시가 아닙니다.",
            ]),
        new(
            "dataframes",
            "데이터 보기",
            "pandas DataFrame 표 보기",
            "DataFrame의 크기, column dtype과 bounded 행·열 표 preview를 확인합니다.",
            "pandas dataframe 데이터프레임 table 표 row column index dtype preview pagination page",
            "pandas가 대상 프로세스에 이미 로드되어 있고 선택한 값이 exact DataFrame일 때 DataFrame 탭이 활성화됩니다. 표는 전체 DataFrame을 직렬화하지 않고 최대 cell budget 안에서 행과 열을 나누어 가져옵니다.",
            [
                "Variables에서 DataFrame 변수를 선택해 DataFrame 탭을 엽니다.",
                "상단에서 전체 shape, 현재 행·열 범위와 column dtype을 확인합니다.",
                "Previous/Next rows와 columns로 bounded page를 이동합니다.",
                "값이 바뀐 뒤 Refresh preview 또는 F5로 현재 page를 다시 읽습니다.",
                "Variables의 이름 검색으로 여러 DataFrame 중 원하는 변수를 빠르게 선택합니다.",
            ],
            """
            import pandas as pd

            df_sales = pd.DataFrame({
                "product": ["A", "B"],
                "quantity": [2, 3],
                "price": [1200, 950],
            })
            df_sales["revenue"] = df_sales["quantity"] * df_sales["price"]
            """,
            [
                "PyMonitor는 DataFrame accessor나 사용자 property를 실행하지 않습니다.",
                "지원하지 않는 extension cell은 안전한 unavailable 값으로 표시될 수 있습니다.",
            ]),
        new(
            "matplotlib",
            "데이터 보기",
            "Matplotlib Figure와 Axes 보기",
            "완료된 Agg render buffer를 안전하게 읽어 Figure preview로 표시합니다.",
            "matplotlib figure axes graph plot chart Agg canvas draw render preview 그래프",
            "Matplotlib이 대상 프로세스에 이미 로드되어 있고 exact regular Figure 또는 Axes를 선택하면 소유 Figure의 마지막 완료된 Agg render를 보여줍니다. PyMonitor는 대상에서 canvas.draw()를 대신 호출하지 않습니다.",
            [
                "사용자 코드에서 Figure를 만들고 fig.canvas.draw()까지 실행합니다.",
                "Variables에서 fig 또는 axes 변수를 선택해 Matplotlib 탭을 엽니다.",
                "Figure 크기와 render 상태를 확인하고 Refresh preview를 누릅니다.",
                "plot을 변경한 뒤 사용자 코드에서 다시 draw하고 PyMonitor preview를 갱신합니다.",
            ],
            """
            import matplotlib.pyplot as plt

            fig_line, ax_line = plt.subplots()
            ax_line.plot([0, 1, 2], [0, 1, 4])
            fig_line.canvas.draw()
            """,
            [
                "draw 전이거나 render 중인 buffer는 stale/unavailable 상태로 표시됩니다.",
                "preview는 최대 1024px 및 4 MiB로 제한됩니다.",
            ]),
        new(
            "memory",
            "진단",
            "Memory와 tracemalloc",
            "Windows 프로세스 메모리와 Python allocation snapshot·diff를 구분해 봅니다.",
            "memory 메모리 working set private bytes virtual peak tracemalloc allocation snapshot diff timeline start stop",
            "Memory 탭은 Windows가 보는 Working Set, Private Bytes, Virtual Size, Peak와 Python allocator의 tracemalloc을 분리해 표시합니다. tracemalloc은 시작 이후의 Python allocation만 추적하며 native, NumPy payload 전체나 GPU 메모리를 뜻하지 않습니다.",
            [
                "대상에 연결한 뒤 Memory 탭에서 OS process memory 값을 확인합니다.",
                "Start tracing으로 tracemalloc을 시작하고 추적 시작 시각과 traceback depth를 확인합니다.",
                "Snapshot을 만든 뒤 대상 코드에서 allocation을 발생시키고 다음 Snapshot을 만듭니다.",
                "Diff에서 파일·라인별 size/count 증감을 확인합니다.",
                "필요한 조사가 끝나면 Stop tracing을 누릅니다.",
            ],
            """
            # snapshot 사이에 실행할 예
            retained = [bytearray(1024) for _ in range(1000)]
            """,
            [
                "tracing 시작 전 allocation은 snapshot에 포함되지 않습니다.",
                "객체 shallow size, 배열 payload와 전체 프로세스 메모리는 서로 다른 지표입니다.",
            ]),
        new(
            "execution-events",
            "진단",
            "Execution Events 모니터링",
            "CPython 3.12+에서 선택한 call/return/line/exception 이벤트를 bounded buffer로 기록합니다.",
            "events execution monitoring sys.monitoring event call return line exception raise yield unwind path prefix CPython 3.12",
            "Events 탭은 CPython 3.12 이상의 sys.monitoring을 사용합니다. 이벤트는 변수 값을 기록하지 않으며 실행 위치와 흐름을 보여주는 별도 기능입니다. 선택 이벤트와 include path prefix를 좁혀 대상 프로그램의 overhead를 관리합니다.",
            [
                "CPython 3.12 이상 대상에 연결하고 Events 탭을 엽니다.",
                "필요한 Start, Return, Yield, Unwind, Raise, Line, Call 이벤트만 선택합니다.",
                "Include path prefix에 사용자 코드 폴더를 지정해 라이브러리 내부 이벤트를 줄입니다.",
                "Start monitoring 후 대상 코드를 실행하고 표의 thread, function, location과 detail을 확인합니다.",
                "수집이 끝나면 Stop을 눌러 대상 overhead를 없앱니다.",
            ],
            "",
            [
                "Python 3.10~3.11에서는 사용할 수 없습니다.",
                "buffer capacity를 넘은 이벤트는 dropped count로 집계됩니다.",
                "기존 debugger/profiler가 monitoring tool ID를 사용 중이면 충돌 오류가 표시될 수 있습니다.",
            ]),
        new(
            "gc-search",
            "진단",
            "GC-tracked objects 검색",
            "명시적 scan으로 GC가 추적하는 객체를 type, module 또는 address로 찾습니다.",
            "GC garbage collector tracked objects heap object search scan pagination type module address 100000",
            "GC-tracked objects는 모든 Python 객체 목록이 아닙니다. 사용자가 Search/Scan을 실행할 때만 bounded snapshot을 만들며, GC가 추적하는 객체 중 type, module 또는 address가 일치하는 결과를 page로 보여줍니다.",
            [
                "Inspect의 Runtime Tree에서 GC-tracked objects를 선택합니다.",
                "검색어와 scan limit를 확인하고 Search/Scan을 실행합니다.",
                "Variables 결과에서 type, module, preview와 address를 확인합니다.",
                "Next/Previous로 page를 이동하고 원하는 객체를 선택해 일반 Object/Class 보기로 이어갑니다.",
            ],
            "",
            [
                "UI scan은 대상 정지를 피하기 위해 최대 100,000개 후보로 제한됩니다.",
                "int, str와 일부 extension 객체처럼 GC가 추적하지 않는 값은 누락될 수 있습니다.",
            ]),
        new(
            "sample-files",
            "예제",
            "함께 제공되는 실습 예제",
            "목적에 맞는 samples 파일을 골라 연결, 객체, 데이터 보기와 launch 동작을 연습합니다.",
            "samples examples 예제 target_ux_demo.py test_python_code.py target_sample.py target_managed.py tutorial demo",
            "설치본과 포터블 배포에는 samples 폴더가 포함됩니다. 처음에는 Managed Launch로 target_ux_demo.py를 실행하는 것이 좋고, VS Code breakpoint와 DataFrame/OpenCV 실습에는 test_python_code.py를 사용합니다.",
            [
                "samples\\target_ux_demo.py: 중첩 객체, cycle, class/method, 자동 변경 변수, 배열과 Figure를 한 번에 봅니다.",
                "samples\\test_python_code.py: VS Code breakpoint에서 pandas와 OpenCV 생성 과정을 한 줄씩 봅니다.",
                "samples\\target_sample.py: Cooperative Attach와 기본 변수·배열 inspection을 연습합니다.",
                "samples\\target_managed.py: argv, cwd, env, stdout/stderr와 exit code 전달을 확인합니다.",
            ],
            """
            # 가장 쉬운 시작
            Launch 탭
              Python: 사용 중인 python.exe
              Script: samples\target_ux_demo.py
              명령: Launch
            """,
            [
                "target_sample.py는 NumPy가 필요하고, test_python_code.py는 NumPy, pandas, Matplotlib, OpenCV가 필요합니다.",
            ]),
        new(
            "troubleshooting",
            "문제 해결",
            "연결 대기, 로딩 및 비활성 탭 해결",
            "계속 로딩되거나 연결되지 않을 때 PID, bootstrap, safe point, stale Agent와 adapter 조건을 확인합니다.",
            "문제 해결 troubleshooting error 오류 loading 로딩 waiting 연결 안됨 timeout permission denied 권한 safe point stale incompatible active conflict agent DataFrame disabled Matplotlib unavailable expired",
            "계속 로딩 중이라면 먼저 하단 상태가 Connected인지, 선택한 PID가 실제 debuggee인지 확인합니다. PyMonitor는 UI를 연결한 것처럼 보이게 추정하지 않으며 Agent handshake가 끝나야 snapshot을 요청합니다. 데이터 전용 탭은 대상 라이브러리와 exact type 조건이 맞을 때만 활성화됩니다.",
            [
                "Not connected: Rescan 후 실제 Python PID를 다시 고르고 Quick Attach 절차를 완료합니다.",
                "Waiting for bootstrap: 3.10~3.13이면 복사된 한 줄을 대상 Python >>> 또는 Debug Console에서 실행합니다.",
                "3.14 timeout: 대상이 blocking call에 있으면 Python safe point로 돌아오도록 실행을 진행하거나 REPL에서 Enter를 누릅니다.",
                "STALE_AGENT/INCOMPATIBLE_AGENT: 대상 Python을 완전히 종료하고 새 프로세스로 다시 연결합니다.",
                "ACTIVE_AGENT_CONFLICT: 기존 PyMonitor를 Detach하거나 대상 Python을 재시작합니다.",
                "DataFrame 비활성: 대상에서 pandas가 로드됐고 선택 값이 exact DataFrame인지 확인합니다.",
                "Matplotlib unavailable: fig.canvas.draw() 완료 후 Refresh preview를 누릅니다.",
                "Object expired: source 또는 변수를 새로 고르고 현재 snapshot에서 객체를 다시 엽니다.",
            ],
            """
            빠른 확인 순서
            1. PID가 맞는가?
            2. 하단 상태가 Connected인가?
            3. 3.10~3.13 bootstrap을 Python 프롬프트에서 실행했는가?
            4. 대상 라이브러리가 이미 import됐는가?
            5. F5로 현재 source를 다시 읽었는가?
            """,
            [
                "권한이 부족한 3.14 live attach는 Elevate live helper를 사용할 수 있지만 GUI 전체를 관리자 권한으로 실행할 필요는 없습니다.",
                "대상과 Inspector snapshot 사이에는 짧은 시간차가 있을 수 있습니다.",
            ]),
        new(
            "shortcuts-safety",
            "참고",
            "단축키와 안전 경계",
            "F1 도움말, 검색·새로 고침·객체 이동 단축키와 read-only 원칙을 확인합니다.",
            "F1 help 도움말 shortcut keyboard 단축키 Ctrl+F F5 Alt+Left Alt+Right back forward read-only safe mode security 안전",
            "PyMonitor의 단축키는 현재 작업 컨텍스트를 빠르게 찾고 갱신하는 용도입니다. 검사는 read-only이며 변수 값, 대상 메모리 또는 사용자 객체를 수정하지 않습니다. Safe Mode는 임의 property, descriptor, repr, getattr와 callable을 실행하지 않습니다.",
            [
                "F1: 검색 가능한 PyMonitor Help를 열고, 이미 열려 있으면 검색란으로 이동합니다.",
                "Ctrl+F: 메인 창에서는 Variables 검색, 도움말 창에서는 도움말 검색란으로 이동합니다.",
                "F5: 메인 창에서 현재 snapshot을 즉시 새로 고칩니다.",
                "Alt+Left / Alt+Right: 선택한 객체 탐색 history를 앞뒤로 이동합니다.",
                "Esc: 도움말 창을 닫습니다.",
            ],
            "",
            [
                "snapshot은 대상 전체를 중단한 원자적 상태가 아니며 수집 중 값이 바뀔 수 있습니다.",
                "PyPy, free-threaded CPython, subinterpreter, embedded Python, x86/ARM64, 원격 PC와 GPU 메모리는 현재 지원하지 않습니다.",
                "사용자가 소유하거나 명시적으로 허가받은 로컬 Python 프로세스만 검사하십시오.",
            ]),
    ]);

    public static IReadOnlyList<HelpTopic> Topics => BuiltInTopics;

    public static IReadOnlyList<HelpTopic> Search(string? query)
    {
        var normalizedQuery = query?.Trim() ?? string.Empty;
        if (normalizedQuery.Length == 0)
            return Topics;

        var tokens = normalizedQuery.Split(
            [' ', '\t', '\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return Topics
            .Select((topic, index) => new
            {
                Topic = topic,
                Index = index,
                Score = Score(topic, normalizedQuery, tokens),
            })
            .Where(match => match.Score >= 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Index)
            .Select(match => match.Topic)
            .ToArray();
    }

    private static int Score(HelpTopic topic, string query, IReadOnlyList<string> tokens)
    {
        if (tokens.Any(token => !topic.SearchDocument.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return -1;

        var score = string.Equals(topic.Title, query, StringComparison.OrdinalIgnoreCase) ? 1_000 : 0;
        foreach (var token in tokens)
        {
            if (topic.Title.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 100;
            else if (topic.Keywords.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 60;
            else if (topic.Summary.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 30;
            else
                score += 10;
        }
        return score;
    }
}
