# Experiments log (append-only)

| Date | Change | Failure class | Tests | Result | Decision |
|---|---|---|---|---|---|
| 2026-11-08 | Median of text count matches | false-positive-extraction | `Stray_sidebar_count_does_not_override_the_typical_value` | pass; existing popularity tests pass | kept |
| 2026-11-08 | Discussion counted in visible text only | false-positive-extraction | `Comment_markup_in_css_and_scripts_...`, `Visible_discussion_still_raises_...` | pass | kept |
| 2026-11-08 | Log-scaled archive signal | saturation/ties | `Archive_signal_keeps_separating_*`, `Archive_signal_is_bounded_and_monotonic`, gold forum ordering | pass; 114 related tests pass | kept |
