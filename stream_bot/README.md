# Stream Bot — Streaming Survival

Публичные команды: `#join` · `#do <текст>`.

LLM выбирает одно действие из whitelist:
`collect_water` · `collect_wood` · `collect_food` · `kill_sheep` · `build_campfire` · `idle`

Unity режим: `-forestStreamingSurvival` (без ML-Agents / без onnx).

Запуск среды на lab:
```bash
bash train_scripts/lab_comp/start_streaming_survival.bash
bash train_scripts/lab_comp/stop_streaming_survival.bash
```

Train AI Forest Survival не трогается — отдельная вкладка UI и отдельные процессы.
