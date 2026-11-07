# usd2glb: a USD stage's meshes as one glb, for a 3D object layer (ADR-283 §5.2).
# Built in the server image's own stage against tinyusdz (Apache-2.0) at the commit
# deploy/server.Dockerfile pins; nothing of tinyusdz is in this repository.
cmake_minimum_required(VERSION 3.16)
project(usd2glb CXX)
set(CMAKE_CXX_STANDARD 14)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

set(TINYUSDZ_DIR "" CACHE PATH "A checkout of tinyusdz")
set(TINYUSDZ_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(TINYUSDZ_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(TINYUSDZ_BUILD_BENCHMARKS OFF CACHE BOOL "" FORCE)
set(TINYUSDZ_WITH_TYDRA ON CACHE BOOL "" FORCE)
add_subdirectory(${TINYUSDZ_DIR} tinyusdz EXCLUDE_FROM_ALL)

add_executable(usd2glb usd2glb.cc)
target_include_directories(usd2glb PRIVATE ${TINYUSDZ_DIR}/src)
target_link_libraries(usd2glb PRIVATE tinyusdz_static)
