#include <sunfleck/ui.hpp>
#include <windows.h>
#include <string>
#include <exception>

__declspec(noinline) int increment(int value)
{
    return value + 1;
}
struct Counter { sunfleck::Node label; int count = 0; std::exception_ptr error; };
static void clicked(ui_node*, void* context) noexcept
{
    auto& state = *static_cast<Counter*>(context);
    try {
        state.count = increment(state.count);
        state.label.setText(("Count: " + std::to_string(state.count)).c_str());
    } catch (...) { state.error = std::current_exception(); }
}

int WINAPI WinMain(HINSTANCE, HINSTANCE, LPSTR, int)
{
    const ui_app_options options{sizeof(options), UI_API_VERSION, UI_SOFTWARE, UI_DEBUG_PIPE};
    sunfleck::App app(options);
    app.loadFleck("<Window xmlns='urn:sunfleck:v1' Title='Sunfleck EnC Acceptance' Width='420' Height='260'><StackPanel><TextBlock Id='count' Text='Count: 0'/><Button Id='increment' Content='Increment'/></StackPanel></Window>");
    app.setFleckSourceUri(SUNFLECK_ENC_PAGE);
    Counter state{app.findFleckNode("count")};
    app.findFleckNode("increment").setAction(clicked, &state);
    app.openWindow();
    app.run();
    return state.error ? 1 : 0;
}
