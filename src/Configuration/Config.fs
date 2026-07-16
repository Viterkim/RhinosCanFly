module RhinosCanFly.Config

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

[<Literal>]
let CurrentVersion = 0

let defaultValues: FlyConfigFile =
    { config_version = CurrentVersion
      forward = "W"
      backward = "S"
      left = "A"
      right = "D"
      up = "Q"
      down = "E"
      boost_toggle = "LeftShift"
      slow = "LeftAlt"
      speed_increase = "Equals"
      speed_decrease = "Minus"
      exit_key = "Escape"
      base_speed = 36.
      minimum_speed = 1.
      maximum_speed = 100000.
      speed_step_multiplier = 1.3
      boost_multiplier = 3.
      slow_multiplier = 0.3
      mouse_sensitivity = 15.
      invert_mouse_y = false
      update_hz = 120.
      normalize_diagonal_movement = true
      wheel_changes_speed = true
      exit_on_mouse_left = false
      exit_on_mouse_right = true
      exit_on_mouse_middle = false
      hijack_right_click_to_enter = true
      boost_hold_instead_of_toggle = false
      slow_hold_instead_of_toggle = false
      vertical_speed_multiplier = 0.6
      lens_length_mm_in_mode = 0. }

let normalize_number (value: float) =
    Math.Round(value, 12, MidpointRounding.AwayFromZero)

let normalize_numbers (source: FlyConfigFile) =
    { source with
        base_speed = normalize_number source.base_speed
        minimum_speed = normalize_number source.minimum_speed
        maximum_speed = normalize_number source.maximum_speed
        speed_step_multiplier = normalize_number source.speed_step_multiplier
        boost_multiplier = normalize_number source.boost_multiplier
        slow_multiplier = normalize_number source.slow_multiplier
        mouse_sensitivity = normalize_number source.mouse_sensitivity
        update_hz = normalize_number source.update_hz
        vertical_speed_multiplier = normalize_number source.vertical_speed_multiplier
        lens_length_mm_in_mode = normalize_number source.lens_length_mm_in_mode }

let options =
    JsonSerializerOptions(
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    )

let mutable settingsRoot: string option = None

let initialize (directory: string) =
    Directory.CreateDirectory directory |> ignore
    settingsRoot <- Some directory

let path () =
    match settingsRoot with
    | Some directory -> Path.Combine(directory, "rhinos-can-fly-config.json")
    | None -> failwith "The RhinosCanFly settings directory has not been initialized."

let default_config () = defaultValues

let to_object (value: FlyConfigFile) =
    JsonSerializer.SerializeToNode(normalize_numbers value, options).AsObject()

let write_json (config_path: string) (json: JsonObject) =
    File.WriteAllText(config_path, json.ToJsonString options + Environment.NewLine)

let merge_known_values (target: JsonObject) (source: FlyConfigFile) =
    for property in to_object source do
        target[property.Key] <- property.Value.DeepClone()

let compile (source: FlyConfigFile) =
    let errors = ResizeArray<string>()

    let required (name: string) (value: string) =
        match KeyBindings.parse value with
        | Ok key -> key
        | Error error ->
            errors.Add $"{name}: {error}"

            { source = if isNull value then "" else value
              virtual_key = 0 }

    let optional (name: string) (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(required name value)

    let positive (name: string) (value: float) =
        if Double.IsNaN value || Double.IsInfinity value || value <= 0. then
            errors.Add $"{name} must be a positive finite number"

    [ "base_speed", source.base_speed
      "minimum_speed", source.minimum_speed
      "maximum_speed", source.maximum_speed
      "speed_step_multiplier", source.speed_step_multiplier
      "boost_multiplier", source.boost_multiplier
      "slow_multiplier", source.slow_multiplier
      "mouse_sensitivity", source.mouse_sensitivity
      "update_hz", source.update_hz
      "vertical_speed_multiplier", source.vertical_speed_multiplier ]
    |> List.iter (fun (name: string, value: float) -> positive name value)

    if source.maximum_speed < source.minimum_speed then
        errors.Add "maximum_speed must be greater than or equal to minimum_speed"

    if
        source.base_speed < source.minimum_speed
        || source.base_speed > source.maximum_speed
    then
        errors.Add "base_speed must be between minimum_speed and maximum_speed"

    if source.speed_step_multiplier <= 1. then
        errors.Add "speed_step_multiplier must be greater than 1"

    if source.update_hz > 1000. then
        errors.Add "update_hz must be 1000 or lower"

    if
        Double.IsNaN source.lens_length_mm_in_mode
        || Double.IsInfinity source.lens_length_mm_in_mode
        || source.lens_length_mm_in_mode < 0.
    then
        errors.Add "lens_length_mm_in_mode must be 0 (disabled) or a positive finite number"

    let result =
        { forward = required "forward" source.forward
          backward = required "backward" source.backward
          left = required "left" source.left
          right = required "right" source.right
          up = required "up" source.up
          down = required "down" source.down
          boost_toggle = required "boost_toggle" source.boost_toggle
          slow = required "slow" source.slow
          speed_increase = optional "speed_increase" source.speed_increase
          speed_decrease = optional "speed_decrease" source.speed_decrease
          exit_key = required "exit_key" source.exit_key
          base_speed = source.base_speed
          minimum_speed = source.minimum_speed
          maximum_speed = source.maximum_speed
          speed_step_multiplier = source.speed_step_multiplier
          boost_multiplier = source.boost_multiplier
          slow_multiplier = source.slow_multiplier
          mouse_sensitivity =
            source.mouse_sensitivity
            |> ConfigMouseSensitivity
            |> MouseSensitivity.to_runtime
          invert_mouse_y = source.invert_mouse_y
          update_hz = source.update_hz
          normalize_diagonal_movement = source.normalize_diagonal_movement
          wheel_changes_speed = source.wheel_changes_speed
          exit_on_mouse_left = source.exit_on_mouse_left
          exit_on_mouse_right = source.exit_on_mouse_right
          exit_on_mouse_middle = source.exit_on_mouse_middle
          hijack_right_click_to_enter = source.hijack_right_click_to_enter
          boost_hold_instead_of_toggle = source.boost_hold_instead_of_toggle
          slow_hold_instead_of_toggle = source.slow_hold_instead_of_toggle
          vertical_speed_multiplier = source.vertical_speed_multiplier
          lens_length_mm_in_mode = source.lens_length_mm_in_mode }

    if errors.Count = 0 then
        Ok result
    else
        Error(String.Join(Environment.NewLine, errors))

let load () =
    try
        let config_path = path ()
        let created = not (File.Exists config_path)

        let json =
            if created then
                to_object defaultValues
            else
                match JsonNode.Parse(File.ReadAllText config_path) with
                | :? JsonObject as value -> value
                | _ -> failwith $"The config root must be a JSON object: {config_path}"

        let messages = ResizeArray<string>()
        let mutable changed = created
        let beforeNormalization = json.ToJsonString()

        if created then
            messages.Add $"created config at {config_path}"

        let defaults = to_object defaultValues

        let knownNames =
            defaults
            |> Seq.map (fun (property: Collections.Generic.KeyValuePair<string, JsonNode>) -> property.Key)
            |> Set.ofSeq

        let unknownNames =
            json
            |> Seq.map (fun (property: Collections.Generic.KeyValuePair<string, JsonNode>) -> property.Key)
            |> Seq.filter (fun (name: string) -> not (knownNames.Contains name))
            |> List.ofSeq

        for name in unknownNames do
            json.Remove name |> ignore
            changed <- true

        if not (List.isEmpty unknownNames) then
            messages.Add $"removed {unknownNames.Length} unknown setting(s)"

        json["config_version"] <- JsonValue.Create CurrentVersion
        let mutable added = 0

        for property in defaults do
            if not (json.ContainsKey property.Key) then
                json[property.Key] <- property.Value.DeepClone()
                added <- added + 1
                changed <- true

        if json.ToJsonString() <> beforeNormalization then
            changed <- true

        if added > 0 then
            messages.Add $"added {added} missing setting(s)"

        let parsed =
            try
                let value = JsonSerializer.Deserialize<FlyConfigFile>(json.ToJsonString(), options)

                if isNull (box value) then
                    Error "the config is empty"
                else
                    Ok value
            with error ->
                Error error.Message

        let source, config =
            match parsed with
            | Ok source ->
                let source = normalize_numbers source

                match compile source with
                | Ok config -> source, config
                | Error _ ->
                    json.Clear()
                    merge_known_values json defaultValues
                    changed <- true
                    messages.Add "reset invalid settings to defaults"

                    match compile defaultValues with
                    | Ok config -> defaultValues, config
                    | Error error -> failwith error
            | Error _ ->
                json.Clear()
                merge_known_values json defaultValues
                changed <- true
                messages.Add "reset malformed settings to defaults"

                match compile defaultValues with
                | Ok config -> defaultValues, config
                | Error error -> failwith error

        let beforeNumberNormalization = json.ToJsonString()
        merge_known_values json source

        if json.ToJsonString() <> beforeNumberNormalization then
            changed <- true

        if changed then
            write_json config_path json

        Ok
            { config_file = source
              config = config
              path = config_path
              created = created
              messages = List.ofSeq messages }
    with error ->
        Error error.Message

let save (source: FlyConfigFile) =
    let normalizedSource = normalize_numbers source

    match compile normalizedSource with
    | Error error -> Error error
    | Ok _ ->
        try
            let config_path = path ()

            let json =
                to_object
                    { normalizedSource with
                        config_version = CurrentVersion }

            let content = json.ToJsonString options + Environment.NewLine

            let existing =
                if File.Exists config_path then
                    File.ReadAllText config_path
                else
                    ""

            if existing <> content then
                File.WriteAllText(config_path, content)

            Ok()
        with error ->
            Error error.Message

let read_raw () =
    try
        let config_path = path ()

        let content =
            if File.Exists config_path then
                File.ReadAllText config_path
            else
                ""

        Ok(config_path, content)
    with error ->
        Error error.Message
