# HRL Option Learning Setup

## Описание
Реализована система Hierarchical Reinforcement Learning (HRL) с option learning для агента выживания в лесу.

### Архитектура:
1. **OptionSelectorAgent** (High-level) - выбирает опцию: 0 (дерево) или 1 (еда)
2. **AgentGoToHouseDiscrete** (Low-level) - выполняет действия согласно выбранной опции

## Настройка в Unity

### 1. Настройка AgentGoToHouseDiscrete (LowLevelAgent):
- Behavior Name: `LowLevelAgent`
- Vector Observations: Size = 13 (опция one-hot: [1,0]=дерево, [0,1]=еда)
- Behavior Type = **Inference Only**, загрузить модель (`.onnx`) от обучения IFF single agent
- Архитектура в YAML: совпадает с IFF_single_agent_256 (256/2). Если обучали через IFF_single_agent.yaml — в HRL-конфигах для LowLevelAgent поставьте 128 и 2

### 2. Настройка OptionSelectorAgent:
- Behavior Name: `OptionSelector`
- Добавить компонент `OptionSelectorAgent` на отдельный GameObject (или на тот же, что и AgentGoToHouseDiscrete)
- В Inspector назначить ссылку на `AgentGoToHouseDiscrete` в поле `Low Level Agent`

### 3. Настройка Action Space для OptionSelectorAgent:
- Discrete Actions: 1 branch, 2 actions (0 или 1)

### 4. Настройка Observation Space для OptionSelectorAgent:
- Vector Observations: Size = 11
  - wood (normalized)
  - heat (normalized)
  - satiety (normalized)
  - position X (normalized)
  - position Z (normalized)
  - distance to house (normalized)
  - has trees nearby (0/1)
  - has sheep nearby (0/1)
  - current option (0/1)
  - time since option selection (normalized)
  - steps since last option change (normalized)

## Обучение (только верхнеуровневая стратегия)

В HRL option learning обучается **только OptionSelector**; низкоуровневый агент работает в **Inference** с весами, полученными при обучении IFF single agent.

1. **Сначала обучить низкоуровневого агента** (если ещё не обучен):
   - Конфиг: `IFF_single_agent.yaml` или `IFF_single_agent_256.yaml`, Behavior Name в Unity должен совпадать с именем в конфиге (для загрузки модели в HRL — в сцене HRL используйте `LowLevelAgent` и загрузите туда сохранённый `.onnx`).

2. **В Unity для HRL:**
   - LowLevelAgent: Behavior Type = **Inference Only**, загрузить модель (`.onnx`) от IFF single agent.
   - OptionSelector: Behavior Type = **Default** (для обучения).

3. **Запуск обучения верхнего уровня:**
```bash
mlagents-learn custom_configs/HRL_option_learning.yaml --run-id=hrl_option_learning
```
   Или тот же режим через отдельный конфиг:
```bash
mlagents-learn custom_configs/HRL_option_selector_only.yaml --run-id=hrl_option_selector_only
```

### Соответствие архитектуры
- В `HRL_option_learning.yaml` и `HRL_option_selector_only.yaml` блок **LowLevelAgent** задан так же, как в **IFF_single_agent_256.yaml** (hidden_units: 256, num_layers: 2), чтобы загружаемая модель подходила по архитектуре.
- Если низкоуровневую обучали конфигом **IFF_single_agent.yaml** (128, 2 слоя), в обоих HRL-конфигах в секции LowLevelAgent замените `hidden_units: 256` и `num_layers: 2` на `128` и `2` в `network_settings` и в `reward_signals.extrinsic.network_settings`.

## Логика работы:

1. **OptionSelectorAgent** наблюдает состояние низкоуровневого агента
2. **OptionSelectorAgent** выбирает опцию (0 или 1) **каждые 10 шагов** (не каждый шаг)
3. **AgentGoToHouseDiscrete** получает опцию и выполняет соответствующие действия
4. Опция остается фиксированной в течение 10 шагов, даже если OptionSelectorAgent пытается её изменить
5. Награды распределяются:
   - LowLevelAgent: за выполнение действий (рубка дерева, поедание овец, движение к цели)
   - OptionSelectorAgent: за правильный выбор опции на основе успешности выполнения задачи

## Reward для OptionSelectorAgent:

- Положительная награда за успешное выполнение выбранной опции
- Штраф за слишком частую смену опций
- Штраф за таймаут опции
- Награда за поддержание баланса ресурсов (тепло/сытость)
