# Env-редактор: редактирование переменных окружения settings.json из окна настроек

## Overview

Добавить в окно настроек ccswitcher редактор переменных окружения из Claude
Code's `settings.json` → `env`. Пользователь получает единый вид всего блока
`env`, разложенного на три корзины, и может редактировать те ключи, которые
безопасно редактировать, без ручной правки JSON-файла.

Проблема, которую решаем: сегодня общие/пользовательские env-ключи вообще
нельзя редактировать в приложении (ccswitcher их не трогает), а `extra_env`
редактируется только внутри диалога аккаунта. Нет единого места, где виден весь
`env`.

Выбранный подход — **Вариант A** (категоризированный редактор): каждая правка
маршрутизируется в свой «дом», оба инварианта проекта соблюдаются. Managed-ключи
показываются read-only, `extra_env` активного аккаунта и общие ключи —
редактируемы, и каждая правка сохраняется корректно и переживает переключение.

## Context (from discovery)

Файлы/компоненты:
- `src-winui/CCSwitcher/Core/SettingsEnv.cs` — `ManagedKeys`, `Load`, `MergeEnv`,
  `CaptureExtraEnv`. Сюда добавляем классификацию и `ApplySharedEnv`.
- `src-winui/CCSwitcher/Core/Models.cs` — `Account.ExtraEnvNullable`/`ExtraEnv`,
  `AppConfig.ManagedKeys`.
- `src-winui/CCSwitcher/Core/Switcher.cs` — `ReapplyActiveAccountEnv(config,
  accountId, ProxyDeps)` уже готова: backup + atomic write `settings.json` +
  config save, трогает только managed-регион. Переиспользуем как есть.
- `src-winui/CCSwitcher/SettingsWindow.xaml` / `.xaml.cs` — секции
  Accounts/Proxy/Startup/On switch; приватный класс `EnvVarEditor` (таблица
  ключ/значение, `.Collect() -> Dictionary<string,string>?`, `.Root`; работает
  только со `string`); хелпер `WidenDialog(ContentDialog, FrameworkElement)` для
  расширения диалога; обёртка `ReapplyActiveEnvIfActive(config, accountId)`.
- `src-winui/CCSwitcher.Tests/` — xUnit, компилирует `Core/**/*.cs` напрямую
  (net8.0), инжектит `InMemorySecretStore`/`InMemoryCredentialStore`.
- `docs/spec.md` — контракт поведения, обновляется в том же изменении.

Паттерны:
- Мутации под `App.StateMutex`; после — `App.RebuildTray()` + `Refresh()`;
  фидбек через `ShowSuccess`/`ShowError`; сообщения через `Secrets.Sanitize`.
- Диалоги строятся императивно в code-behind как `ContentDialog`.
- Core-классы stateless; всё нужное принимают параметрами.

Зависимости: `SettingsEnv.ManagedKeys` = { `ANTHROPIC_BASE_URL`,
`ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY`, `HTTP_PROXY`, `HTTPS_PROXY`,
`NO_PROXY` }. `config.ManagedKeys` = точный набор ключей, записанный ccswitcher в
`env` последним (managed-константы активного аккаунта + его `extra_env`).

## Development Approach

- **Testing approach**: Regular с уклоном в «тесты рядом» — как в проекте
  (тесты пишутся сразу после/вместе с core-кодом; UI code-behind тестами не
  покрывается по устройству тест-проекта).
- Завершать каждую задачу полностью до перехода к следующей.
- Небольшие сфокусированные изменения.
- **Каждая задача с изменением core-кода ОБЯЗАНА включать новые/обновлённые
  тесты** (успех + ошибки/крайние случаи). UI-задачи (code-behind/XAML) тестами
  не покрываются — это ограничение тест-проекта (см. CLAUDE.md).
- **Все тесты должны проходить до начала следующей задачи.**
- Обновлять этот план при изменении объёма.
- Сохранять обратную совместимость.

## Testing Strategy

- **Unit tests**: обязательны для каждой core-задачи (классификация,
  `ApplySharedEnv`). Тест-проект компилирует только `Core/`, поэтому UI не
  тестируется автоматически.
- **E2E tests**: в проекте нет UI e2e-фреймворка (WinUI 3 tray-app). UI-поведение
  проверяется вручную (см. Post-Completion).
- Команды:
  - `cd src-winui && dotnet test CCSwitcher.Tests/CCSwitcher.Tests.csproj`
  - `cd src-winui && dotnet build CCSwitcher.sln`

## Progress Tracking

- Отмечать выполненное `[x]` сразу.
- Новые задачи — с префиксом ➕.
- Блокеры — с префиксом ⚠️.
- Держать план в синхроне с фактической работой.

## Solution Overview

Классификация живого `env` из `settings.json` на три корзины (Managed /
AccountExtra / Shared) выполняется чистым stateless-хелпером в `Core/`. UI
показывает три группы в одном `ContentDialog`; редактируемы только AccountExtra
и Shared. При Save правки идут двумя независимыми путями в непересекающиеся
области `env`:

- **AccountExtra** → записывается в `active.ExtraEnvNullable`, затем
  `Switcher.ReapplyActiveAccountEnv` (готовая функция) перестраивает
  managed-регион и делает атомарную запись + config save.
- **Shared** → новый core-метод `SettingsEnv.ApplySharedEnv` делает точечный
  touched-only мердж (удаляет убранные ключи, пишет оставшиеся, не трогает
  managed и `extra_env`), затем backup + atomic write через `AtomicFile`.

Ключевое проектное решение: два отдельных вызова вместо одного объединённого —
минимум нового кода, максимум переиспользования; лишний backup дёшев. Обе части
трогают разные ключи, поэтому порядок не важен и частичный сбой не создаёт
рассинхрона (повторный Save до-применит).

## Technical Details

**Классификация ключа `env`:**
- `Managed` — ключ ∈ `SettingsEnv.ManagedKeys`.
- `AccountExtra` — ключ ∈ `active.ExtraEnv.Keys` и ∉ `ManagedKeys` (managed
  выигрывает при коллизии имён).
- `Shared` — всё остальное.
- Managed-порядок фиксированный (как в `ManagedKeys`); прочее — порядок из файла
  (стабильный вывод для тестов).
- **Крайний случай:** нестроковые значения shared-ключей (число/массив/объект)
  нельзя редактировать текстовым полем → выносятся в отдельный **read-only**
  список, в редактируемую Shared-корзину НЕ попадают и при Save НЕ удаляются
  (не входят в `oldSharedKeys`). Managed и `extra_env` у нас всегда строки.

**Тип результата (набросок):**
```csharp
public sealed record EnvBuckets(
    IReadOnlyList<KeyValuePair<string,string>> Managed,       // read-only, строки
    IReadOnlyList<KeyValuePair<string,string>> AccountExtra,  // редактируемо
    IReadOnlyList<KeyValuePair<string,string>> Shared,        // редактируемо (строки)
    IReadOnlyList<string> SharedReadOnlyKeys);                // нестроковые shared
```

**`ApplySharedEnv` (набросок):**
```csharp
public static JsonObject ApplySharedEnv(
    JsonObject settings,
    IEnumerable<string> oldSharedKeys,               // строковые shared при открытии
    IReadOnlyDictionary<string,string> newShared)    // из редактора
// взять/создать env; удалить те oldSharedKeys, что отсутствуют в newShared;
// записать newShared; managed и extra_env НЕ трогать. Вернуть settings.
```

**Поток Save (UI, под `App.StateMutex`, отпуск в `finally`):**
0. Под мьютексом **перечитать** активный аккаунт из свежего `config` (диалог —
   снимок, снятый вне мьютекса; между открытием и Save мог пройти switch из трея).
1. Валидация: пересечение shared-ключей → `ShowError`, не сохранять. Проверять
   пересечение **и** с `SettingsEnv.ManagedKeys` (константные managed-имена),
   **и** с ключами `extra_env` активного аккаунта (набор из редактора AccountExtra).
   Иначе новый shared-ключ с именем активного `extra_env`-ключа сломает
   непересекаемость корзин и рассинхронит `settings.json` с `config.ManagedKeys`.
2. AccountExtra (если активный аккаунт есть и набор изменился): обновить
   `active.ExtraEnvNullable` (или `null` если пусто) → `ReapplyActiveEnvIfActive`.
3. Shared (если изменился): загрузить `settings.json` (`SettingsEnv.Load`),
   `ApplySharedEnv`, backup + atomic write через `AtomicFile`.
4. `App.RebuildTray()` + `Refresh()` + `ShowSuccess`. Диалог закрыть только при
   успехе.

**Открытие диалога:** `SettingsEnv.Load`; если JSON битый — не открывать,
`ShowError`, ничего не писать.

## What Goes Where

- **Implementation Steps** (`[ ]`): core-хелперы + тесты, UI-диалог и секция,
  обновление `docs/spec.md` и `CLAUDE.md`.
- **Post-Completion** (без чекбоксов): ручная проверка UI-сценариев (нет e2e).

## Implementation Steps

### Task 1: Классификация env — `SettingsEnv.ClassifyEnv` + `EnvBuckets`

**Files:**
- Modify: `src-winui/CCSwitcher/Core/SettingsEnv.cs`
- Modify: `src-winui/CCSwitcher.Tests/SettingsEnvTests.cs` (или создать
  `EnvBucketsTests.cs` рядом, если файла нет)

- [x] добавить `public sealed record EnvBuckets(...)` (Managed, AccountExtra,
      Shared, SharedReadOnlyKeys) в `Core/` (в `SettingsEnv.cs` или соседний файл)
- [x] реализовать `public static EnvBuckets ClassifyEnv(JsonObject settings,
      Account? active)`: читать только `env`-объект; managed по `ManagedKeys`
      (фиксированный порядок), AccountExtra по `active?.ExtraEnv.Keys` минус
      managed, остальное — Shared; нестроковые значения shared → `SharedReadOnlyKeys`
- [x] managed/AccountExtra значения приводить к строке (они всегда строки);
      shared со строковым значением → Shared, иначе → read-only
- [x] тест: пустой/отсутствующий `env` → все корзины пустые
- [x] тест: только shared-ключи, `active == null` → всё в Shared, AccountExtra пуст
- [x] тест: активный аккаунт с `extra_env` → ключи в AccountExtra, не в Shared
- [x] тест: коллизия имени managed и extra_env → ключ в Managed (managed выигрывает)
- [x] тест: нестроковое shared-значение (число/массив) → в `SharedReadOnlyKeys`,
      не в Shared
- [x] запустить тесты — должны пройти до Task 2

### Task 2: Точечная запись shared — `SettingsEnv.ApplySharedEnv`

**Files:**
- Modify: `src-winui/CCSwitcher/Core/SettingsEnv.cs`
- Modify: `src-winui/CCSwitcher.Tests/SettingsEnvTests.cs`

- [x] реализовать `public static JsonObject ApplySharedEnv(JsonObject settings,
      IEnumerable<string> oldSharedKeys, IReadOnlyDictionary<string,string> newShared)`
- [x] логика: взять/создать `env`; удалить `oldSharedKeys` отсутствующие в
      `newShared`; записать пары `newShared`; managed и `extra_env` не трогать
- [x] тест: добавление нового shared-ключа → появляется в `env`
- [x] тест: удаление (ключ в `oldSharedKeys`, нет в `newShared`) → исчезает
- [x] тест (Инвариант 1): managed-ключи и `extra_env`-ключи не тронуты
- [x] тест: read-only нестроковый ключ (не в `oldSharedKeys`) переживает вызов
- [x] тест: отсутствующий/непустой/не-объектный `env` → создаётся/переиспользуется
      корректно
- [x] запустить тесты — должны пройти до Task 3

### Task 3: UI — секция «Environment» и диалог редактора

**Files:**
- Modify: `src-winui/CCSwitcher/SettingsWindow.xaml`
- Modify: `src-winui/CCSwitcher/SettingsWindow.xaml.cs`

- [x] добавить в XAML секцию «Environment» после «Proxy»: `SettingsCard` с
      иконкой + кнопка «Manage…» (`Click="ManageEnvBtn_Click"`)
- [x] в code-behind: обработчик открывает `ContentDialog`, расширенный
      `WidenDialog`; при открытии `SettingsEnv.Load` + `ClassifyEnv` (битый JSON →
      `ShowError`, не открывать)
- [x] построить три группы: (1) Managed — read-only карточки, **токен маскируется**
      (`••••••`/`(set)`, секрет не показываем), подпись «редактируй в диалоге
      аккаунта / Proxy», скрыть если нет активного аккаунта; (2) AccountExtra —
      `EnvVarEditor` из `extra_env` активного, скрыть если нет активного; (3) Shared —
      `EnvVarEditor` из строковых shared-ключей; read-only shared показать
      отдельной подписью/строками
- [x] параметризовать заголовок/подпись `EnvVarEditor` (сейчас захардкожено
      «Environment variables (optional)» / «Applied … when this account is active»)
      или подавить встроенную подпись и дать секции Shared собственный заголовок —
      иначе для shared-корзины текст вводит в заблуждение
- [x] обернуть панель из трёх групп во внешний `ScrollViewer` внутри контента
      `WidenDialog` (окно всего 680 dip; managed-карточки + read-only список +
      три таблицы могут не влезть по высоте)
- [x] кнопки Save/Cancel; диалог — снимок (читаем при открытии, применяем при Save)
- [x] UI-код тестами не покрывается (ограничение тест-проекта — только `Core/`);
      корректность обеспечивается тестами Task 1–2 и ручной проверкой

### Task 4: UI — маршрутизация Save

**Files:**
- Modify: `src-winui/CCSwitcher/SettingsWindow.xaml.cs`

- [x] реализовать Save под `App.StateMutex` (отпуск в `finally`)
- [x] под мьютексом перечитать активный аккаунт из свежего `config` (защита от
      switch из трея между открытием диалога и Save)
- [x] валидация: пересечение shared-ключей (`envEditor.Collect()`) с
      `SettingsEnv.ManagedKeys` **и** с ключами `extra_env` активного аккаунта
      (набор AccountExtra) → `ShowError` («нельзя переопределять ключ X»),
      не сохранять, диалог не закрывать
- [x] AccountExtra: если активный аккаунт есть и набор изменился —
      `active.ExtraEnvNullable = collected ?? null` → `ReapplyActiveEnvIfActive`
- [x] Shared: если изменился — `SettingsEnv.Load` → `ApplySharedEnv(settings,
      oldSharedStringKeys, newShared)` → backup + atomic write через `AtomicFile`
- [x] после успеха: `App.RebuildTray()` + `Refresh()` + `ShowSuccess`; ошибки через
      `Secrets.Sanitize`; диалог закрыть только при успехе
- [x] UI-код тестами не покрывается; логика роутинга `extra_env` уже покрыта
      тестами `ReapplyActiveAccountEnv`

### Task 5: Обновить документацию контракта

**Files:**
- Modify: `docs/spec.md`
- Modify: `CLAUDE.md`

- [ ] `docs/spec.md`: описать env-редактор, три корзины, правило классификации,
      shared-запись как точечный touched-only мердж, read-only нестроковых
      значений, запрет shared-ключей с managed-именами
- [ ] `CLAUDE.md` (Инвариант 1): добавить строку, что пользователь может явно
      редактировать shared-ключи через env-редактор — единственный путь, которым
      ccswitcher пишет не-managed ключи; managed-регион и `extra_env` по-прежнему
      не переписываются вслепую
- [ ] сверить формулировки с реальным поведением из Task 1–4

### Task 6: Verify acceptance criteria

- [ ] проверить, что все требования из Overview реализованы
- [ ] проверить крайние случаи (нет активного аккаунта; битый JSON; нестроковые
      shared; коллизия имён с managed)
- [ ] полный прогон: `cd src-winui && dotnet test CCSwitcher.Tests/CCSwitcher.Tests.csproj`
- [ ] сборка: `cd src-winui && dotnet build CCSwitcher.sln`
- [ ] e2e в проекте нет — отметить как N/A

### Task 7: [Final] Финализация плана

- [ ] обновить README.md при необходимости
- [ ] обновить CLAUDE.md если найдены новые паттерны (сверх Task 5)
- [ ] переместить план в `docs/plans/completed/`

## Post-Completion

*Ручные шаги — без чекбоксов, информационно.*

**Ручная проверка UI** (нет e2e-фреймворка):
- Открыть «Environment» → «Manage…» с активным аккаунтом: видны три группы,
  токен замаскирован.
- Добавить/изменить/удалить shared-ключ → Save → проверить `settings.json`:
  изменился только shared-ключ, managed и `extra_env` целы.
- Изменить `extra_env` активного аккаунта в редакторе → Save → значение
  применилось в `settings.json` немедленно и сохранилось в аккаунте.
- Ввести shared-ключ с managed-именем (`ANTHROPIC_BASE_URL`) → Save → ошибка,
  файл не изменён.
- Открыть при отсутствии активного аккаунта → только группа Shared.
- Повредить `settings.json` вручную → «Manage…» → ошибка, редактор не
  открывается, запись не выполняется.
- Проверить нестроковый shared-ключ (например `env: { "FOO": 123 }`) → показан
  read-only, после Save не удалён.

**Публикация** (при релизе):
- Пересобрать self-contained `.exe`:
  `dotnet publish CCSwitcher/CCSwitcher.csproj -c Release -r win-x64
  --self-contained true -p:PublishSingleFile=true -o publish/`
