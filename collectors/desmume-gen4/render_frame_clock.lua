local M = {}

function M.install()
    if emu == nil or type(emu.framecount) ~= "function" then
        return
    end

    local native_framecount = emu.framecount

    emu.framecount = function(...)
        local level = 2
        while level <= 5 do
            local info = debug.getinfo(level, "S")
            if info == nil then
                break
            end

            if info.what ~= "C" then
                local source = string.gsub(info.source or "", "\\", "/")
                if source == "@soulbuddy_all.lua" or
                   string.sub(source, -18) == "/soulbuddy_all.lua" then
                    return nil
                end
                break
            end

            level = level + 1
        end

        return native_framecount(...)
    end
end

return M
