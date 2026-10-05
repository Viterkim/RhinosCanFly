namespace RhinosCanFly

type KeyBinding =
    { keys: BindingToken array
      native_keys: int array
      unsupported: BindingToken option }
