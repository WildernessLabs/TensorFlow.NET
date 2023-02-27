﻿/*****************************************************************************
   Copyright 2018 The TensorFlow.NET Authors. All Rights Reserved.

   Licensed under the Apache License, Version 2.0 (the "License");
   you may not use this file except in compliance with the License.
   You may obtain a copy of the License at

       http://www.apache.org/licenses/LICENSE-2.0

   Unless required by applicable law or agreed to in writing, software
   distributed under the License is distributed on an "AS IS" BASIS,
   WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
   See the License for the specific language governing permissions and
   limitations under the License.
******************************************************************************/

using Tensorflow.NumPy;
using System;
using System.Linq;
using System.Collections.Generic;
using Tensorflow.Functions;
using Tensorflow.Graphs;
using static Tensorflow.Binding;
using static Tensorflow.Graphs.SubGraphUtility;

namespace Tensorflow.Keras
{
    public class BackendImpl : BackendBase
    {
        /* ----------------------------------------  KERAS BACKEND NATIVE OBJECTS  ---------------------------------------- */
        public Func<Array, double> py_sum = sum;
        public Func<Array, bool> py_all = all;
        //Func<Array, bool> py_any = any;
        //Func<double, double, double, IEnumerable<double>> py_slice = slice;

        public Session _SESSION => ops.get_default_session();

        public Graph _GRAPH;
        FuncGraph _CURRENT_SCRATCH_GRAPH;
        public Dictionary<Graph, GraphLearningPhase> _GRAPH_LEARNING_PHASES;
        //Dictionary<Graph, Dictionary<string, int>> PER_GRAPH_LAYER_NAME_UIDS;
        public bool _MANUAL_VAR_INIT = false;
        public List<string> _LOCAL_DEVICES = null;
        /* --------------------------------------  KERAS BACKEND NATIVE OBJECTS END  -------------------------------------- */

        /// <summary>
        /// A global dictionary mapping graph objects to an index of counters used
        /// for various layer names in each graph.
        /// Allows to give unique autogenerated names to layers, in a graph-specific way.
        /// </summary>
        public Dictionary<Graph, Dictionary<string, int>> PER_GRAPH_LAYER_NAME_UIDS = new Dictionary<Graph, Dictionary<string, int>>();
        public Dictionary<string, IVariableV1> _GRAPH_VARIABLES = new Dictionary<string, IVariableV1>();
        public Dictionary<string, Optimizer> _GRAPH_TF_OPTIMIZERS = new Dictionary<string, Optimizer>();

        public _DummyEagerGraph _DUMMY_EAGER_GRAPH = new _DummyEagerGraph();

        public BackendImpl()
        {
        }

        public void track_variable(IVariableV1 v)
        {
            var graph = v.Graph;
            _GRAPH_VARIABLES[graph.graph_key] = v;
        }

        public Tensor placeholder(Shape shape = null,
            int ndim = -1,
            TF_DataType dtype = TF_DataType.DtInvalid,
            bool sparse = false,
            string name = null,
            bool ragged = false)
        {
            if (sparse)
            {
                throw new NotImplementedException("placeholder sparse is true");
            }
            else
            {
                return array_ops.placeholder(dtype: dtype, shape: shape, name: name);
            }
        }

        public Graph get_graph()
        {
            if (tf.Context.executing_eagerly())
            {
                if (_GRAPH == null)
                    _GRAPH = new FuncGraph("keras_graph");
                
                return _GRAPH;
            }
            return ops.get_default_graph();
        }

        FuncGraph _scratch_graph()
        {
            if (_CURRENT_SCRATCH_GRAPH == null)
                _CURRENT_SCRATCH_GRAPH = new FuncGraph("keras_scratch_graph");
            
            return _CURRENT_SCRATCH_GRAPH;
        }

        public int get_uid(string prefix)
        {
            var graph = tf.get_default_graph();
            if (!PER_GRAPH_LAYER_NAME_UIDS.ContainsKey(graph))
                PER_GRAPH_LAYER_NAME_UIDS.Add(graph, new defaultdict<string, int>());
            if (!PER_GRAPH_LAYER_NAME_UIDS[graph].ContainsKey(prefix))
                PER_GRAPH_LAYER_NAME_UIDS[graph][prefix] = 0;
            PER_GRAPH_LAYER_NAME_UIDS[graph][prefix] += 1;

            return PER_GRAPH_LAYER_NAME_UIDS[graph][prefix];
        }

        public void reset_uids() => PER_GRAPH_LAYER_NAME_UIDS = new Dictionary<Graph, Dictionary<string, int>>();
        public void clear_session()
        {
            tf.Context.reset_context();
            reset_uids();
            // var phase = tf.placeholder_with_default(false, new int[] { }, name: "keras_learning_phase");
            if (_GRAPH_LEARNING_PHASES != null)
                _GRAPH_LEARNING_PHASES.Clear();
            if (_GRAPH_LEARNING_PHASES != null)
                _GRAPH_LEARNING_PHASES.Clear();
            PER_GRAPH_LAYER_NAME_UIDS.Clear();
            _CURRENT_SCRATCH_GRAPH = null;
            _GRAPH = null;

            ops.set_default_session(tf.Session(ops.get_default_graph()));
            tf.enable_eager_execution();
            tf.Runner.ClearEagerOperationMap();

            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        public void manual_variable_initialization(bool value)
        {
            _MANUAL_VAR_INIT = value;
        }

        public Tensor mean(Tensor x, int axis = -1, bool keepdims = false)
        {
            if (x.dtype.as_base_dtype() == TF_DataType.TF_BOOL)
                x = math_ops.cast(x, TF_DataType.TF_FLOAT);
            return math_ops.reduce_mean(x, axis: axis, keepdims: false);
        }

        public GraphLearningPhase learning_phase()
        {
            var graph = tf.get_default_graph();
            if (_GRAPH_LEARNING_PHASES.ContainsKey(graph))
            {
                var phase = tf.placeholder_with_default(false, shape: new int[] { }, name: "keras_learning_phase");
                _GRAPH_LEARNING_PHASES[graph] = 0;
            }
            return _GRAPH_LEARNING_PHASES[graph];
        }
        public void set_learning_phase(bool value)
        {
            _GRAPH_LEARNING_PHASES[tf.get_default_graph()] = (GraphLearningPhase)((value) ? 1 : 0);
        }

        public void batch_set_value(List<(IVariableV1, NDArray)> tuples)
        {
            if (ops.executing_eagerly_outside_functions())
            {
                foreach (var (x, value) in tuples)
                    x.assign(value, read_value: false);
            }
            else
            {
                throw new NotImplementedException("");
            }
        }

        /// <summary>
        /// Pads the 2nd and 3rd dimensions of a 4D tensor.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="padding"></param>
        /// <param name="data_format"></param>
        /// <returns></returns>
        public Tensor spatial_2d_padding(Tensor x, NDArray padding = null, string data_format = null)
        {
            if (padding == null)
                padding = new[,] { { 1, 1 }, { 1, 1 } };

            NDArray pattern;

            if (data_format == "channels_first")
                pattern = new int[,]
                {
                    { 0, 0 },
                    { 0, 0 },
                    { padding[0][0], padding[0][1] },
                    { padding[1][0], padding[1][1] }
                };
            else
                pattern = new int[,]
                {
                    { 0, 0 },
                    { padding[0][0], padding[0][1] },
                    { padding[1][0], padding[1][1] },
                    { 0, 0 }
                };
            return array_ops.pad(x, pattern);
        }

        /// <summary>
        /// Method to evaluate a tensor in eager or in a tf.function.
        /// </summary>
        /// <param name="outputs"></param>
        /// <returns></returns>
        public NDArray eval_in_eager_or_function(Tensors outputs)
        {
            if (outputs[0].op.type == "Const")
                return tensor_util.constant_value(outputs);
                
            var source_graph = outputs.graph;
            var exec_graph = _scratch_graph();
            var global_graph = get_graph();
            if (source_graph == global_graph && exec_graph != global_graph)
            {
                var lifted_map = lift_to_graph(outputs, exec_graph, 
                    new List<Tensor>(), 
                    add_sources: true, 
                    handle_captures: true, 
                    base_graph: source_graph);
            }
            if (outputs[0].op.type == "Placeholder"
                || outputs[0].op.type == "StridedSlice")
                return exec_graph.external_captures.Last().numpy();

            // Consolidate updates
            exec_graph.as_default();
            exec_graph.Inputs = exec_graph.internal_captures;
            exec_graph.Outputs = outputs;
            
            var graph_fn = new ConcreteFunction(exec_graph);

            _CURRENT_SCRATCH_GRAPH = null;
            tf.Context.restore_mode();
            // return outputs.eval();
            throw new NotImplementedException("");
        }

        public class _DummyEagerGraph
        { }

        /// <summary>
        /// Categorical crossentropy between an output tensor and a target tensor.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="output"></param>
        /// <param name="from_logits"></param>
        /// <param name="axis"></param>
        /// <returns></returns>
        public Tensor categorical_crossentropy(Tensor target, Tensor output, bool from_logits = false, int axis = -1)
        {
            if (from_logits)
                return tf.nn.softmax_cross_entropy_with_logits_v2(labels: target, logits: output, axis: axis);

            if (output.op != null && output.op.type == "Softmax")
            {
                if (output.op.inputs.Length != 1) throw new ApplicationException();
                var o = output.op.inputs[0];
                return tf.nn.softmax_cross_entropy_with_logits_v2(labels: target, logits: o, axis: axis);
            }

            // scale preds so that the class probas of each sample sum to 1
            output = output / math_ops.reduce_sum(output, new Axis(axis), true);
            // Compute cross entropy from probabilities.
            var epsilon_ = constant_op.constant(epsilon(), output.dtype.as_base_dtype());
            output = clip_ops.clip_by_value(output, epsilon_, 1.0f - epsilon_);
            return -math_ops.reduce_sum(target * math_ops.log(output), new Axis(axis));
        }

        public Tensor sparse_categorical_crossentropy(Tensor target, Tensor output, bool from_logits = false, int axis = -1, int? ignore_class = null)
        {
            target = tf.cast(target, tf.int64);
            if (!from_logits)
            {
                var epsilon_ = constant_op.constant(epsilon(), output.dtype.as_base_dtype());
                output = tf.clip_by_value(output, epsilon_, 1 - epsilon_);
                output = tf.math.log(output);
            }
            var output_rank = output.shape.ndim;
            if (output_rank > -1)
            {
                axis = Math.Abs(axis) % output_rank;
                if (axis != output_rank - 1)
                {
                    /*var permutation = list(
                        itertools.chain(
                            range(axis), range(axis + 1, output_rank), [axis]
                        )
                    );
                    output = tf.transpose(output, perm: permutation);*/
                    throw new NotImplementedException("");
                }

            }

            var output_shape = tf.shape(output);
            var target_rank = target.shape.ndim;
            var update_shape = target_rank > -1 && output_rank > -1 && target_rank != output_rank - 1;
            if (update_shape)
            {
                /*var target = flatten(target);
                output = tf.reshape(output, [-1, output_shape[-1]]);*/
                throw new NotImplementedException("");
            }

            if (ignore_class.HasValue)
            {
                throw new NotImplementedException("");
            }

            var res = tf.nn.sparse_softmax_cross_entropy_with_logits(labels: target, logits: output);

            if (ignore_class.HasValue)
            {
                throw new NotImplementedException("");
            }

            if (update_shape && output_rank >= 3)
            {
                // If our output includes timesteps or
                // spatial dimensions we need to reshape
                res = tf.reshape(res, output_shape[":-1"]);
            }

            return res;
        }

        public Tensor binary_crossentropy(Tensor target, Tensor output, bool from_logits = false)
        {
            if (from_logits)
                return tf.nn.sigmoid_cross_entropy_with_logits(labels: target, logits: output);

            var epsilon_ = constant_op.constant(epsilon(), dtype: output.dtype.as_base_dtype());
            output = tf.clip_by_value(output, epsilon_, 1.0f - epsilon_);

            // Compute cross entropy from probabilities.
            var bce = target * tf.math.log(output + epsilon());
            bce += (1 - target) * tf.math.log(1 - output + epsilon());
            return -bce;
        }

        /// <summary>
        /// Resizes the images contained in a 4D tensor.
        /// </summary>
        /// <param name="x"></param>
        /// <param name="height_factor"></param>
        /// <param name="width_factor"></param>
        /// <param name="data_format"></param>
        /// <param name="interpolation"></param>
        /// <returns></returns>
        public Tensor resize_images(Tensor x, int height_factor, int width_factor, 
            string data_format, string interpolation = "nearest")
        {
            var (rows, cols) = (0, 0);
            if (data_format == "channels_first")
                (rows, cols) = (2, 3);
            else if (data_format == "channels_last")
                (rows, cols) = (1, 2);
            else
                throw new ValueError($"Invalid `data_format` argument: {data_format}");

            var original_shape = x.shape;
            var new_shape = array_ops.shape(x)[new Slice(rows, cols + 1)];
            new_shape *= constant_op.constant(np.array(height_factor, width_factor));

            if (data_format == "channels_first")
                // x = permute_dimensions(x, [0, 2, 3, 1]);
                throw new NotImplementedException("");
            if (interpolation == "nearest")
                x = tf.image.resize_images_v2(x, new_shape, method: ResizeMethod.NEAREST_NEIGHBOR);

            if (data_format == "channels_first")
                // x = permute_dimensions(x, [0, 3, 1, 2]);
                throw new NotImplementedException("");

            int new_height = original_shape[rows] < 0 ? -1 : (int)original_shape[rows] * height_factor;
            int new_width = original_shape[cols] < 0 ? -1 : (int)original_shape[cols] * width_factor;

            Shape output_shape = data_format == "channels_first" ?
                (-1, -1, new_height, new_width) : (-1, new_height, new_width, -1);
            x.shape = output_shape;
            return x;
        }

        /// <summary>
        /// Concatenates a list of tensors alongside the specified axis.
        /// </summary>
        /// <param name="tensors">list of tensors to concatenate.</param>
        /// <param name="axis">concatenation axis.</param>
        /// <returns></returns>
        public Tensor concatenate(Tensors tensors, int axis = -1)
        {
            if(axis < 0)
            {
                var rank = tensors[0].ndim;
                if (rank > -1)
                    axis += rank;
                else
                    axis = 0;
            }

            return array_ops.concat(tensors, axis);
        }

        public Tensor conv2d_transpose(Tensor x,
                     IVariableV1 kernel,
                     Tensor output_shape,
                     Shape strides = null,
                     string padding = "valid",
                     string data_format = null,
                     Shape dilation_rate = null)
        {
            /*
            var force_transpose = false;
            if (data_format == "channels_first" && !dilation_rate.Equals(new[] { 1, 1 }))
                force_transpose = true;
            x, tf_data_format = _preprocess_conv2d_input(x, data_format, force_transpose)
            */
            var tf_data_format = "NHWC";
            padding = padding.ToUpper();
            strides = new Shape(1, strides[0], strides[1], 1);
            if (dilation_rate.Equals(new long[] { 1, 1 }))
                x = nn_impl.conv2d_transpose(x, kernel, output_shape, strides,
                    padding: padding,
                    data_format: tf_data_format);
            else
                throw new NotImplementedException("dilation_rate other than [1,1] is not yet supported");

            return x;
        }
    }
}
