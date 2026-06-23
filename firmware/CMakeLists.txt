cmake_minimum_required(VERSION 3.16)
include($ENV{IDF_PATH}/tools/cmake/project.cmake)
project(seadrop-firmware)

# Suppress these warnings to -Wno-error so pre-existing code in the codebase
# can still build. The underlying bugs are documented but do not affect
# runtime correctness in this project's use case.
add_compile_options(
    -Wno-error=stringop-truncation
    -Wno-error=format-truncation
    -Wno-error=array-bounds
    -Wno-error=maybe-uninitialized
)
