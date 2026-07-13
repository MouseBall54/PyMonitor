# Embedded console namespaces

Python의 일반 대화형 프롬프트는 보통 `__main__.__dict__`에 변수를 선언하므로
PyMonitor의 **Modules / __main__**에서 볼 수 있습니다. 반면 프로그램 안에 포함된
터미널은 `code.InteractiveConsole.locals`, IPython `_user_ns`, 또는 애플리케이션이
관리하는 별도 딕셔너리를 사용할 수 있습니다. 명령 실행이 끝나면 실행 frame은
사라지지만 이 owner와 namespace가 살아 있는 동안 **Console namespaces**에서 계속
조회할 수 있습니다.

## 자동 탐지

연결하거나 Runtime Tree를 새로 고칠 때 Agent는 `gc.get_objects()` snapshot의 앞쪽
최대 100,000개 owner를 검사합니다. `gc.collect()`는 호출하지 않습니다.

- 이미 로드된 `code.InteractiveInterpreter`와 `code.InteractiveConsole` 계열의
  direct `locals` dictionary
- 이미 로드된 IPython `InteractiveShell` 계열의 direct `_user_ns` backing field
- leaf class 이름에 `console`, `interactive`, `repl`, `shell`, `terminal`이 들어가고
  알려진 namespace field에 exact `dict`를 보관하는 custom owner

탐지는 owner의 direct CPython instance dictionary와 built-in `dict` 연산만 사용합니다.
`getattr`, property, descriptor, callable, 사용자 `repr`은 실행하지 않습니다. 같은
mapping을 여러 owner가 가리키면 identity로 중복을 제거하고 표준/IPython source를
custom heuristic보다 우선합니다. 한 번에 최대 100개 namespace만 표시합니다.

## 명시적 등록

사후 상태만으로는 일반 dictionary가 `exec`용 namespace였는지 판별할 수 없습니다.
그 경우 애플리케이션이 Agent package를 import할 수 있는 시점에 exact dictionary를
등록합니다.

```python
from pyruntime_inspector_agent import register_namespace, unregister_namespace

namespace = {}
registration_id = register_namespace("작업 콘솔", namespace)

try:
    exec("job_name = 'inspection'", namespace)
finally:
    unregister_namespace(registration_id)
```

등록 이름은 256자, 등록 수는 100개로 제한됩니다. 같은 dictionary의 중복 등록은
거부됩니다. 등록은 dictionary를 강하게 참조하므로 콘솔 수명이 끝날 때 반드시
`unregister_namespace()`를 호출해야 합니다. 반환값은 등록이 실제로 해제되었는지를
나타내는 `bool`입니다.

## 변경 추적과 범위

Console namespace를 선택하면 Variables pagination, 검색·필터, object/class/array
상세 보기와 주기적 refresh를 기존 frame/module scope와 동일하게 사용합니다. 같은
이름의 추가, 삭제, 재바인딩과 bounded metadata 변화는 기존 snapshot 규칙으로
표시됩니다. Global Search에서는 console namespace가 직접 root가 되므로 결과 위치가
`Console namespaces / <owner> / <variable>`로 표시됩니다.

탐지 한도 밖의 owner는 Runtime Tree refresh에서 누락될 수 있으며 이 경우 scan이
incomplete로 표시됩니다. owner와 namespace의 모든 강한 참조가 사라진 뒤에는
복구할 수 없습니다. 별도 subprocess나 subinterpreter의 namespace는 현재 연결의
대상이 아니므로 해당 프로세스에 별도로 attach해야 합니다.
