# CLAUDE.md — руководство для ИИ-агента по репозиторию LuaCsForBarotrauma

Прочитайте этот файл целиком перед любой работой. Он описывает, что это за проект, как он устроен,
как собирать и проверять изменения и чего делать нельзя. Если вы не уверены в факте — проверьте его в коде,
а не полагайтесь на память о ванильной игре: этот форк отличается (см. раздел 5).

## 1. Что это

- **Barotrauma** (игра, C#/.NET 8, MonoGame) плюс **LuaCsForBarotrauma** (LuaCs) — встроенный слой моддинга:
  Lua (форк MoonSharp), C#-плагины, Harmony-патчи, события, сетевая обвязка.
- Версия игры в `csproj`: `1.13.4.0`. Целевая платформа: `net8.0`, `LangVersion latest`.
- Код игры проприетарный (см. `EULA.txt`). Не копируйте код куда-либо за пределы репозитория и не публикуйте его.
- Дополнительные документы: `README.md`, `CONTRIBUTING.md`, `PERFORMANCE_PLAN.md` (поэтапный план оптимизации; читать перед любыми правками производительности).

## 2. Структура репозитория

```
/                              корень: .sln, документы, CI
├── Barotrauma/
│   ├── BarotraumaShared/      общий код и общие ресурсы (SharedSource/ — .cs; Data/, LocalMods/, ModLists/, *.props)
│   ├── BarotraumaClient/      клиент (ClientSource/ — .cs; Shaders/, нативные библиотеки; сборка Barotrauma)
│   ├── BarotraumaServer/      выделенный сервер (ServerSource/ — .cs; сборка DedicatedServer)
│   └── BarotraumaTest/        тесты xUnit (Linux/Mac/WindowsTest.csproj)
├── Libraries/                 сторонние и внутренние библиотеки (часть — git-сабмодули)
├── Deploy/                    релизная сборка (DeployAll — программа .NET, скрипты, patches)
├── BuildScripts/              манифесты SteamPipe (*.vdf)
├── luacs-docs/                документация LuaCs (cs — Doxygen, lua — LDoc, landing-page)
├── changelogs/                скрипты объединения changelog
└── .github/workflows/         CI (сборка, тесты, релизы, документация)
```

Решения: `LinuxSolution.sln`, `MacSolution.sln`, `WindowsSolution.sln`. Для Linux проекты: `LinuxClient.csproj`, `LinuxServer.csproj`, `LinuxTest.csproj`.

### 2.1. Клиент, сервер и общий код — самое важное для понимания

- И `LinuxClient.csproj`, и `LinuxServer.csproj` компилируют **весь** `BarotraumaShared/**/*.cs`
  (в серверном проекте: `<Compile Include="..\BarotraumaShared\**\*.cs" />`) плюс собственные `ClientSource` / `ServerSource`.
- Различия задаются **символами условной компиляции**: `CLIENT` (клиент) и `SERVER` (сервер), а также `LINUX`/`WINDOWS`/`OSX`, `DEBUG`, `X64`, `UNSTABLE`.
  Вы часто увидите `#if CLIENT ... #elif SERVER ... #endif` внутри общего кода.
- Класс обычно **разбит на partial-файлы** в трёх местах: `SharedSource/.../X.cs`, `ClientSource/.../X.cs`, `ServerSource/.../X.cs`.
  Общая логика — в Shared, отрисовка/ввод/звук — в Client, сетевая авторитарная логика — в Server.
  Метод `partial void Foo()` в Shared реализуется в Client и/или Server.
- Следствие: **изменение общего кода нужно проверять сборкой и клиента, и сервера.** Символ, доступный только в одном из проектов, ломает другой.
- Имя сборки: клиент — `Barotrauma`, сервер — `DedicatedServer`. Общее ядро выделено в `Libraries/BarotraumaLibs/BarotraumaCore`.

### 2.2. Карта `BarotraumaShared/SharedSource`

| Каталог/файл | Что там |
|---|---|
| `Map/` | `Entity`, `MapEntity`, `Hull`, `Gap`, `Structure`, `Submarine`, `SubmarineBody`, `WayPoint`, `Levels/`, `Outposts/`, `Explosion`, `FireSource`, `EntityGrid` |
| `Items/` | `Item`, `ItemPrefab`, `Inventory`; **`Items/Components/`** — компоненты предметов (`Door`, `Turret`, `Holdable/`, `Machines/`, `Power/`, `Signal/` и т. д.) |
| `Characters/` | `Character`, здоровье/афликции, `Animation/` (`Ragdoll`, `Limb`), **`AI/`** (`HumanAIController`, `EnemyAIController`, `PathFinder`, `Objectives/`, `ShipCommand/`) |
| `StatusEffects/` | `StatusEffect` — движок XML-эффектов; `DelayedEffect`, условия (`PropertyConditional`) |
| `Physics/` | Обёртка над Farseer: `PhysicsBody`, `Physics` |
| `GameSession/` | `GameSession`, `GameModes/` (кампания, PvP и т. д.), `CrewManager`, `CargoManager`, `UpgradeManager` |
| `Events/` | Система событий раунда: `EventManager`, `EventSet`, `Missions/`, `EventActions/` (скриптовые события) |
| `Networking/` | Клиент-серверный слой на Lidgren/Steam/EOS: `NetworkMember`, `Client`, `ServerSettings`, `NetEntityEvent/`, `Voip/`, `FileTransfer/`, `INetSerializableStruct` |
| `Prefabs/` + `ContentManagement/` | Загрузка контента из XML: `Prefab`, `PrefabCollection`, `ContentPackage`, `ContentFile/` |
| `Serialization/` | Сериализация свойств через атрибуты (`SerializableProperty`, `Editable`), XML |
| `Screens/` | `Screen`, `GameScreen`, `NetLobbyScreen` (клиентские экраны — в `ClientSource/Screens`) |
| `Settings/` | `GameSettings`, конфигурация |
| `Utils/` | `Rand` (ГСЧ), `ToolBox`, `CrossThread`, `SaveUtil` и др. |
| `Timing.cs` | Фиксированный шаг обновления 60 Гц, накопитель |
| `PerformanceCounter.cs` | Встроенный счётчик производительности (`showperf` в консоли) |
| `LuaCs/` | Слой LuaCs (см. раздел 4) |
| Прочее | `CircuitBox/`, `Upgrades/`, `Traitors/`, `Decals/`, `Steam/`, `Eos/`, `ProcGen/`, `Text/`, `Sprite/`, `DebugConsole.cs`, `CoroutineManager.cs` |

### 2.3. Ресурсы и моды внутри репозитория

- `BarotraumaShared/Content/` (арт, XML предметов/персонажей, звуки) **отсутствует в репозитории**. Его копируют из легальной копии игры.
  Без него нельзя запустить раунд, поэтому реальный запуск игры и бенчмарки на сохранениях недоступны.
- `BarotraumaShared/Data/` — конфигурационные XML (карма, права, языки, настройки кампании).
- `BarotraumaShared/LocalMods/`: `LuaCsForBarotrauma` (сам пакет LuaCs) и тестовые моды `[DebugOnlyTest]...` (только для Debug-сборок).
- `Libraries/`: `moonsharp` (сабмодуль, форк), `Farseer Physics Engine 3.5` (физика), `Lidgren.Network` (сеть), `MonoGame.Framework`,
  `Facepunch.Steamworks`, `Concentus` (Opus), `SharpFont`, `OpenAL-Soft`, `GameAnalytics`, `Hyper.ComponentModel`, `XNATypes`,
  `webm_mem_playback`, `BarotraumaLibs/{BarotraumaCore,EosInterface,EosInterfacePrivate}`.
  Клонируйте с `--recurse-submodules` или выполните `git submodule update --init --recursive`.

## 3. Как устроена игра (ментальная модель)

### 3.1. Иерархия основных типов

```
Entity  (Map/Entity.cs)                         числовой ID, реестр сущностей
├── MapEntity                                   всё, что размещается на карте/в подлодке
│   ├── Item          (компоненты: ItemComponent → Powered, Door, Turret, ...)
│   ├── Structure     (стены)
│   ├── Hull          (отсек: вода, воздух, огонь)
│   ├── Gap           (проём между отсеками)
│   └── WayPoint, LinkedSubmarine, ...
├── Character         (Ragdoll → Limb; AIController → HumanAIController / EnemyAIController)
├── Submarine, Level
└── EntitySpawner     (очередь создания/удаления сущностей)
```

- Данные из XML описываются **префабами**: `ItemPrefab`, `StructurePrefab`, `CharacterPrefab`, `EventPrefab` и т. д. (`Prefab`, `PrefabCollection`, идентификаторы `Identifier`).
- Списки-реестры статические: `Item.ItemList`, `Character.CharacterList`, `Hull.HullList`, `Gap.GapList`, `MapEntity.MapEntityList`,
  `Powered.PoweredList`, `PhysicsBody.List`. Их порядок и содержимое неявно влияют на логику.

### 3.2. Игровой цикл

- `GameMain.Update` (клиент: `BarotraumaClient/ClientSource/GameMain.cs`) накапливает время и вызывает обновление **с фиксированным шагом 1/60 с**
  (`Timing.Step`). Если накопитель превышает `Timing.AccumulatorMax` (0,25 с), лишнее отбрасывается (игра замедляется).
- Порядок обновления раунда — `SharedSource/Screens/GameScreen.cs` → `Update`: `LightManager` (клиент) → `PhysicsBody.Update` →
  `GameSession.Update` (события, режим, миссии) → частицы (клиент) → `Level.Update` → `Character.UpdateAll` → `StatusEffect.UpdateAll` →
  `MapEntity.UpdateAll` (отсеки, шлюзы, энергосеть `Powered.UpdatePower`, предметы `Item.Update`, спавнер) → `Character.UpdateAnimAll` →
  `Ragdoll.UpdateAll` → `Submarine.Update` → `GameMain.World.Step` (физика Farseer).
- Всё выполняется **в одном потоке**. Порядок шагов важен; не переставляйте их без веской причины.
- Отрисовка — `GameScreen.Draw` / `DrawMap` (клиент); использует отсечение (`Submarine.CullEntities`) и `LightManager`.

### 3.3. Сеть и детерминированность

- Клиент-серверная модель: сервер авторитарен. Сущности синхронизируются событиями (`NetEntityEvent`) и структурами `INetSerializableStruct`.
- `Rand` (`Utils/Rand.cs`) имеет режимы `RandSync.Unsynced`, `ServerAndClient`, `ClientOnly`. Синхронизируемые ГСЧ **нельзя** вызывать из других потоков
  и нельзя менять число/порядок вызовов в коде, влияющем на согласованность клиента и сервера.

### 3.4. Данные и XML

- Контент-пакеты (`ContentPackage`) загружаются через `ContentPackageManager`; файлы разных типов — `ContentFile/`.
- Свойства сериализуются атрибутами (`[Serialize]`, `[Editable]`, ...) через `SerializableProperty`. Изменение имени свойства ломает XML контента и моды.

## 4. Слой LuaCs (`SharedSource/LuaCs/`)

Это основное отличие форка от ванили. Архитектура — сервисы с внедрением зависимостей (LightInject) и событийная шина.

| Часть | Файлы | Назначение |
|---|---|---|
| Точка входа | `LuaCsSetup.cs` (+ partial в `ClientSource/LuaCs`, `ServerSource/LuaCs`) | Синглтон; регистрирует сервисы, ведёт конечный автомат запуска |
| DI | `_Services/ServicesProvider.cs`, `_Services/_Interfaces/I*Service.cs` | `IService`, `IServicesProvider`; регистрация в `SetupServicesProvider` |
| События | `_Services/EventService.cs`, `IEvents.cs` | `IEvent<T>`, `PublishEvent<T>`, Lua-алиасы (`think` → `IEventUpdate`), «старые» строковые хуки (`Call`) |
| Harmony | `_Services/HarmonyEventPatchesService.cs` | Патчи на игровые методы, которые публикуют события (`Character.DamageLimb`, `Affliction.Update`, `Connection.SendSignal`, `Item.Use`, `Inventory.PutItem`, `CoroutineManager.Update`, …) |
| Lua | `_Services/LuaScriptManagementService.cs`, `_Services/_Lua/*` | Загрузка скриптов, `LuaPatcherService` (динамические патчи из Lua через Sigil/IL), `DefaultLuaRegistrar`, `LuaClasses/` (`Game`, `Timer`, `Steam`, `Logger`…) |
| Плагины C# | `_Plugins/*`, `_Services/PluginManagementService.cs` | Загрузка сборок-плагинов, `AssemblyLoader`, `IAssemblyPlugin` |
| Пакеты и конфиг | `PackageManagementService`, `ConfigService`, `ModConfigService`, `Data/` | Разбор `RunConfig.xml`, настройки модов |
| Сеть | `NetworkingService.cs`, `_Networking/` | Сетевые сообщения между Lua/C# модами |
| Совместимость | `Compatibility/` | Устаревший API для старых модов (`ILuaCsHook`, `LuaCsTimer`, ...) |

Зависимости: `Luatrauma.props` (HarmonyX, Sigil, LightInject, OneOf, FluentResults, Roslyn, MoonSharp). Мод-API строится на
**публицизированных сборках** (`AssemblyPublicizer`, `LuatraumaBuild.props`), поэтому моды видят приватные члены игры.

Тесты LuaCs: старые `BarotraumaTest/LuaCs/` (`HookPatchTests`, `LuaCsFixture`) **закомментированы** и не выполняются; работающие — `BarotraumaTest/Performance/EventServiceCharacterizationTests.cs`
(тесты `EventService` с подменой зависимостей через `DispatchProxy`, папка `Content` не нужна). Тесты `Quirk_*` закрепляют поведение, похожее на случайное: меняйте его только осознанно. Документация: `luacs-docs/` (Doxygen для C#, LDoc для Lua).

## 5. Правила совместимости (обязательные)

1. **Не менять сигнатуры существующих членов**, даже приватных: моды компилируются против публицизированных сборок,
   а Lua/Harmony-патчи находят методы по имени класса, имени метода и типам параметров. Добавлять новое можно.
2. **Не превращать поле в свойство и наоборот** (например, `Item.IsActive` — публичное поле, в него пишут моды).
3. **Не менять сигнатуры методов, на которые навешены патчи** в `HarmonyEventPatchesService` и `LuaPatcherService`.
4. **Сохранять порядок обновления** сущностей; сохранять семантику хуков (при нескольких подписчиках `Call<T>` возвращает последнее не-nil значение в порядке обхода). Порядок обхода подписчиков событий **не определён** (хеш словаря): не полагайтесь на него.
5. **Не менять формат сохранений, сетевые протоколы и имена XML-свойств** без явной задачи.
6. **Не трогать `RandSync.ServerAndClient`/`ClientOnly`** и не вызывать их вне главного потока.
7. **Не параллелить основную симуляцию.** Многопоточность допустима только для независимых задач (например, расчёты света уже в отдельном потоке).
8. **Изменение общего кода = проверка клиента и сервера** (символы `CLIENT`/`SERVER`).
9. **Значения игрового баланса и настроек по умолчанию не менять «заодно».**

## 6. Сборка, тесты, запуск

Требуется **.NET 8 SDK** (проверьте `dotnet --version`). В облачной среде без него: `apt-get install -y dotnet-sdk-8.0` (сайты Microsoft недоступны, apt Ubuntu доступен).
Без сабмодуля MoonSharp проекты не собираются: `GIT_LFS_SKIP_SMUDGE=1 git submodule update --init --depth 1 Libraries/moonsharp`.
Первая сборка клиента/сервера занимает 1–2 минуты; запускайте долгие команды с большим `timeout`. Не используйте `pkill -f` с текстом из своей же команды (убьёт вашу оболочку).

```bash
git submodule update --init --recursive                       # сабмодули (moonsharp, ldoc)
dotnet build LinuxSolution.sln -c Release /p:Platform=x64      # всё решение
dotnet build Barotrauma/BarotraumaClient/LinuxClient.csproj -c Debug /p:Platform=x64
dotnet build Barotrauma/BarotraumaServer/LinuxServer.csproj -c Debug /p:Platform=x64
mkdir -p ~/".local/share/Daedalic Entertainment GmbH/Barotrauma"   # как в CI перед тестами
dotnet test LinuxSolution.sln                                  # тесты
Deploy/DeployAll.sh                                            # релизная сборка → Barotrauma/Deploy/bin/content
```

- Конфигурации: `Debug` (доп. консольные команды, тестовые моды, исключения без крэш-отчёта), `Release`, `Unstable`.
- В `csproj` клиента и сервера многие предупреждения о nullable (`CS86xx`) объявлены **ошибками** (`WarningsAsErrors`). Пишите код с учётом nullable там, где включён `#nullable enable`.
- Тестовый проект ссылается на клиент и сервер (`Aliases="Client"` / `"Server"`); стек: xUnit, FluentAssertions, FsCheck.
- **Без папки `Content` игра не запустится.** Автоматические тесты, не требующие контента, работают; проверки «в игре» требуют локальной копии игры.
- CI (`.github/workflows`): `on-push-pr.yml` (PR → безопасность + тесты), `on-push-other-branch.yml` (тесты для веток, кроме master),
  `build.yml` (сборка через `DeployAll` с патчами из `Deploy/patches`), `run-tests.yml`, `update-docs.yml`, `create-prerelease.yml`, `publish-release.yml`.

## 7. Как работать над задачей

1. **Прочитайте задачу и найдите код по имени** (`Grep`), а не по номеру строки. Номера строк в документах устаревают.
2. Определите, где живёт код: Shared, Client или Server. Если файл partial — найдите все части (`Grep "partial class X"`).
3. Проверьте, не затрагивается ли мод-API или патчи (раздел 5): `Grep` по `HarmonyEventPatchesService`, `Hook.Patch`, `luacs-docs`.
4. Делайте **минимальные изменения**, в стиле окружающего кода (4 пробела, UTF-8 с BOM для `.cs`/`.sln`, `csharp_prefer_braces = when_multiline`; см. `.editorconfig`).
5. Для оптимизаций сначала измерьте (см. `PERFORMANCE_PLAN.md`), затем напишите тест на текущее поведение, затем меняйте.
6. Соберите и клиент, и сервер, запустите тесты. Если не можете (нет .NET/Content) — **прямо скажите, что не проверяли**. Не заявляйте о прохождении непроверенного.
7. Не отключайте и не удаляйте тесты ради «зелёного» результата.
8. Не изменяйте `Libraries/` (сторонний код и сабмодули), файлы `*.props` и `Deploy/` без прямой необходимости.

## 8. Работа с git в этом репозитории

- Разрабатывайте в ветке, указанной в задаче (например, `claude/...`); не пушьте в другие ветки без разрешения.
- **Не создавайте pull request, пока пользователь явно не попросил.** Если PR уже есть, новые коммиты в ветку обновляют его.
- Сообщения коммитов — информативные: что и зачем изменено (см. `CONTRIBUTING.md`). Не указывайте идентификатор модели в коммитах, PR и коде.
- Публикация действий во внешний мир (push, комментарии, PR) должна выполняться осознанно; перед необратимыми действиями уточняйте.

## 9. Быстрые ориентиры «где что искать»

| Задача | Куда смотреть |
|---|---|
| Поведение предмета | `Items/Item.cs`, `Items/Components/<Компонент>.cs`, XML префаба в `Content` |
| Сигналы и провода | `Items/Components/Signal/` (`Connection`, `Wire`), `CircuitBox/` |
| Электросеть | `Items/Components/Power/Powered.cs`, `PowerTransfer`, `PowerContainer` |
| Вода/давление/пожар | `Map/Hull.cs`, `Map/Gap.cs`, `Map/FireSource.cs` |
| Урон и здоровье | `Characters/Health/`, `Characters/Character.cs`, `Attack` |
| ИИ ботов и монстров | `Characters/AI/` (`Objectives/` для целей ботов, `EnemyAIController` для монстров, `PathFinder`) |
| Статус-эффекты XML | `StatusEffects/StatusEffect.cs` |
| Физика | `Physics/`, `Libraries/Farseer Physics Engine 3.5/`, `Characters/Animation/Ragdoll.cs` |
| Сеть | `Networking/`, `ClientSource/Networking`, `ServerSource/Networking` |
| Интерфейс | `BarotraumaClient/ClientSource/GUI/` |
| Свет и рендер | `ClientSource/Map/Lights/`, `ClientSource/Screens/GameScreen.cs` |
| Звук | `ClientSource/Sounds/` |
| Консольные команды | `SharedSource/DebugConsole.cs`, `ClientSource/DebugConsole.cs`, `ServerSource/DebugConsole.cs` |
| Настройки игры | `Settings/GameSettings.cs`, `ClientSource/Settings` |
| Lua/C# моддинг | `SharedSource/LuaCs/`, `luacs-docs/`, `LocalMods/LuaCsForBarotrauma` |

## 10. Известные особенности и ловушки

- Многие «списки» — статические публичные (`Item.ItemList` и др.); их изменение во время итерации приводит к `InvalidOperationException`
  (см. обработку в `MapEntity.UpdateAll`). Копируйте перед итерацией, если код может удалять элементы.
- Удаление объектов часто идёт через очередь `Entity.Spawner` (`EntitySpawner`), а не немедленно.
- `Character.Enabled`, `Item.IsActive`, `PhysicsBody.Enabled` управляют тем, обновляется ли объект. Ошибки здесь приводят к «замерзшим» объектам или лишней нагрузке.
- В общем коде клиентские API (`GUI`, `SoundPlayer`, `Camera`) недоступны в серверной сборке — используйте `#if CLIENT`.
- Тестовые моды `[DebugOnlyTest]...` попадают только в Debug-сборки (исключены из Release в `Content Include`).
- Профилирование: консоль `showperf`; счётчики `PerformanceCounter` (`Update:*`, `Draw:*`) уже расставлены в `GameScreen`.
